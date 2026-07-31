using System;
using NUnit.Framework;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class AddressedPredictionRecordFailureTests
    {
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            NetworkManager.CallAllRegisters();
            Hasher.PrepareType<PredictedObjectID>();
            Hasher.PrepareType<PredictedComponentID>();
        }

        [Test]
        public void StateDecodeFailureRejectsTheContainingFrame()
        {
            using var frame = BitPackerPool.Get();
            using var payload = BitPackerPool.Get();
            var id = new PredictedComponentID(new PredictedObjectID(5), 2);

            payload.WriteBits(0b101, 3);
            AddressedPredictionRecords.WriteSectionCount(1, frame);
            AddressedPredictionRecords.WriteRecord(frame, id, false, payload);
            frame.ResetPositionAndMode(true);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                AddressedPredictionRecords.ReadSection(
                    source: frame,
                    readRecord: (_, _, _, _) => throw new ApplicationException("bad state")));

            Assert.That(exception.InnerException, Is.TypeOf<ApplicationException>());
        }

        [Test]
        public void AddressedHelpersDoNotReplaceTheGlobalIntegerPacker()
        {
            const int sentinel = 123456789;
            using var packer = BitPackerPool.Get();

            packer.AdvanceBits(5);
            Packer<int>.Write(packer, sentinel);

            packer.ResetPositionAndMode(true);
            packer.SetBitPosition(5);

            Assert.That(Packer<int>.Read(packer), Is.EqualTo(sentinel));
        }
    }
}
