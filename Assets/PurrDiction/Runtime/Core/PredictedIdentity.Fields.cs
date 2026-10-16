using System;
using System.Collections.Generic;
using PurrNet.Packing;
using Unity.Profiling;

namespace PurrNet.Prediction
{
    // Partial declarations compile in file-name order, so these fields lead the object layout.
    // Every tick reads them for every identity; keeping them together keeps those reads on few
    // cache lines instead of spreading them between rarely used setup and diagnostics fields.
    public abstract partial class PredictedIdentity
    {
        public PredictionManager predictionManager { get; protected set; }

        PlayerID? _owner;
        // _owner in CompactIdentityState's encoding, so ownership checks and snapshots read
        // two fields instead of unwrapping the nullable player.
        internal ulong ownerIdValue;
        internal byte ownerFlags;

        /// <summary>
        /// The unique identifier for this object.
        /// Can be used to identify the object across the network.
        /// </summary>
        [NonSerialized]
        public PredictedComponentID id;

        public ulong? lastVerifiedTick { get; internal set; }

        // Marks the state section that last decoded a record for this identity.
        internal uint addressedReadStamp;

        // Marks the verified tick whose authoritative input was last applied to this identity.
        internal uint verifiedInputStamp;

        protected readonly ProfilerMarker simulateMarker;

        private readonly List<PredictedModule> _modules = new();

        /// <summary>
        /// How this identity participates in client-side prediction and reconciliation.
        /// See <see cref="PredictionPolicy"/> for the semantics of each mode.
        /// </summary>
        public PredictionPolicy predictionPolicy { get; private set; }
        private PredictionPolicy _lastEffectivePredictionPolicy;
        private bool _hasLastEffectivePredictionPolicy;

        private PredictedHierarchy _rootCacheHierarchy;
        private uint _rootCacheRevision;
        private PredictedObjectID _rootCacheObjectId;
        private PredictedObjectID _rootCacheId;

        // Bits of the owner's upload that the server consumed for a tick, kept so the
        // transcript can tell that owner to restore them instead of echoing them.
        private BitPacker _uploadedInputBits;
        private ulong _uploadedInputTick;
        private bool _uploadedInputPending;
        private bool _hasUploadedInputBits;
        // The tick's input is the decoded upload itself, so the uploaded bits decode to it on every peer.
        private bool _uploadedInputVerbatim;
    }
}
