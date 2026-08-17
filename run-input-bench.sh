#!/usr/bin/env bash
# Input bandwidth benchmark: N players with worst-case (constantly changing) input payloads,
# reporting per-peer transport KB/s plus input upload / frame section breakdowns.
#
# usage: run-input-bench.sh <name> [port] [latencyMs] [lossPercent] [noise] [clients] [seconds] [extra args...]
#   noise = full | smooth | constant   (input churn per tick; full = new random vectors every tick)
#
# Requires a DEVELOPMENT player build (the latency/loss simulation is compiled out of release):
#   Unity.exe -batchmode -nographics -projectPath . -executeMethod LocalTestBuild.BuildWindowsDev
set -u

ROOT="$(cd "$(dirname "$0")" && pwd)"
BIN="${BIN:-$ROOT/test-results/suite-player/PurrDictionTests.exe}"
OUT="${OUT:-$ROOT/test-results/input-bench}"

NAME="${1:?usage: run-input-bench.sh <name> [port] [latencyMs] [lossPercent] [noise] [clients] [seconds]}"
PORT="${2:-7801}"
LAT="${3:-0}"
LOSS="${4:-0}"
NOISE="${5:-full}"
CLIENTS="${6:-1}"
SECONDS_ARG="${7:-15}"
shift 1
for _ in 1 2 3 4 5 6; do [ $# -gt 0 ] && shift; done
EXTRA=("$@")

if [ ! -f "$BIN" ]; then
    echo "ERROR: player binary not found at $BIN (run LocalTestBuild.BuildWindowsDev first)"
    exit 1
fi

COUNT=$((CLIENTS + 1))
RES="$OUT/$NAME"; rm -rf "$RES"; mkdir -p "$RES"

SHARED=(-batchmode -nographics -count "$COUNT" -port "$PORT" -connectTimeout 180
  -inputBandwidthBenchmark -ibSeconds "$SECONDS_ARG" -ibNoise "$NOISE"
  -latencyMin "$LAT" -latencyMax "$LAT" -packetLoss "$LOSS")

"$BIN" "${SHARED[@]}" "${EXTRA[@]}" -role host \
  -results "$RES/host.json" -logFile "$RES/host.log" &
HOST_PID=$!

READY=0
for _ in $(seq 1 90); do
  if grep -q 'local connection ready' "$RES/host.log" 2>/dev/null; then READY=1; break; fi
  if ! kill -0 "$HOST_PID" 2>/dev/null; then break; fi
  sleep 1
done
if [ "$READY" -ne 1 ]; then
  echo "host never became ready ($NAME)"
  tail -n 40 "$RES/host.log" 2>/dev/null
  kill -9 "$HOST_PID" 2>/dev/null
  exit 1
fi

CLIENT_PIDS=()
for i in $(seq 1 "$CLIENTS"); do
  "$BIN" "${SHARED[@]}" "${EXTRA[@]}" -role client -serverHost 127.0.0.1 \
    -results "$RES/client-$i.json" -logFile "$RES/client-$i.log" &
  CLIENT_PIDS+=($!)
done

FAIL=0
for pid in "${CLIENT_PIDS[@]}"; do
  wait "$pid" || { echo "client exited nonzero ($NAME)"; FAIL=1; }
done
wait "$HOST_PID" || { echo "host exited nonzero ($NAME)"; FAIL=1; }

echo "=== $NAME summary (latency=${LAT}ms loss=${LOSS}% noise=$NOISE players=$COUNT) ==="
python - "$RES" <<'EOF'
import json, sys, glob, os
for f in sorted(glob.glob(os.path.join(sys.argv[1], '*.json'))):
    print('=====', os.path.basename(f))
    try:
        data = json.load(open(f))
    except Exception as e:
        print('  parse error:', e); continue
    for s in data:
        if s is None:
            print('  <scenario did not run>'); continue
        r = s.get('result') or {}
        print(' ', s.get('name'), 'PASS' if r.get('success') else 'FAIL')
        msg = r.get('message') or ''
        if msg:
            print('   ', msg[:3000])
EOF
exit $FAIL
