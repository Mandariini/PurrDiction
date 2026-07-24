using System.Collections.Generic;

namespace PurrNet.Prediction
{
    /// <summary>
    /// Permanent deletes are transient in the hierarchy state. Keep a receiver-local
    /// tombstone through its reliable delete frame and the following baseline cleanup.
    /// </summary>
    internal sealed class PlayerPendingVisibilityDeletes
    {
        struct Entry
        {
            public PredictedObjectID objectId;
            public PredictedObjectID rootId;
            public List<InstanceDetails> records;
            public ulong observedTick;
            public ulong preparedTick;
            public ulong sentTick;
        }

        readonly List<Entry> _entries = new ();

        public int Count => _entries.Count;

        public void Capture(
            PredictedObjectID objectId,
            PredictedObjectID rootId,
            List<InstanceDetails> records,
            ulong observedTick)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].objectId.Equals(objectId))
                    return;
            }

            var snapshot = new List<InstanceDetails>(records.Count);
            snapshot.AddRange(records);
            _entries.Add(new Entry
            {
                objectId = objectId,
                rootId = rootId,
                records = snapshot,
                observedTick = observedTick
            });
        }

        public void MarkSent(ulong tick)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.sentTick != 0 || entry.preparedTick != tick)
                    continue;

                entry.sentTick = tick;
                _entries[i] = entry;
            }
        }

        public void Acknowledge(ulong acknowledgedTick)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                ulong sentTick = _entries[i].sentTick;
                if (sentTick != 0 && sentTick < acknowledgedTick)
                    _entries.RemoveAt(i);
            }
        }

        public void PrepareCurrent(ulong tick, ref PredictedHierarchyState state)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.sentTick != 0)
                    continue;

                AppendEntry(entry, ref state);
                entry.preparedTick = tick;
                _entries[i] = entry;
            }
        }

        public void PrepareBaseline(
            ulong baselineTick,
            ref PredictedHierarchyState state)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.sentTick == baselineTick)
                    AppendEntry(entry, ref state);
            }
        }

        public bool RequiresFullFrame(ulong tick)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.sentTick == 0 && entry.observedTick < tick)
                    return true;
            }

            return false;
        }

        static void AppendEntry(
            in Entry entry,
            ref PredictedHierarchyState state)
        {
            for (var i = 0; i < entry.records.Count; i++)
            {
                var record = entry.records[i];
                bool alreadyPresent = false;

                for (var j = 0; j < state.spawnedPrefabs.Count; j++)
                {
                    if (!state.spawnedPrefabs[j].instanceId.Equals(record.instanceId))
                        continue;

                    alreadyPresent = true;
                    break;
                }

                if (!alreadyPresent)
                    state.spawnedPrefabs.Add(record);
            }

            for (var i = 0; i < state.toDelete.Count; i++)
            {
                if (state.toDelete[i].Equals(entry.objectId))
                    return;
            }

            state.toDelete.Add(entry.objectId);
        }

        public bool ContainsRoot(PredictedObjectID rootId)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].rootId.Equals(rootId))
                    return true;
            }

            return false;
        }

        public void Clear()
        {
            _entries.Clear();
        }
    }
}
