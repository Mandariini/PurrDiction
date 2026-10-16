using System.Collections.Generic;
using NUnit.Framework;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class PredictionObjectSetTests
    {
        [Test]
        public void MembershipMatchesHashSetAcrossGrowthSparseIdsAndReuse()
        {
            var actual = new PredictionObjectSet();
            var expected = new HashSet<PredictedObjectID>();
            uint random = 0xC0FFEE;
            for (int cycle = 0; cycle < 4; cycle++)
            {
                foreach (uint value in new uint[] { 0, 1, uint.MaxValue, uint.MaxValue - 1, 1U << 31 })
                {
                    var id = new PredictedObjectID(value);
                    Assert.That(actual.Add(id), Is.EqualTo(expected.Add(id)));
                    Assert.That(actual.Add(id), Is.False);
                }
                for (uint i = 0; i < 2048; i++)
                {
                    var id = new PredictedObjectID(cycle % 2 == 0 ? i : i << 16);
                    Assert.That(actual.Add(id), Is.EqualTo(expected.Add(id)));
                    random ^= random << 13;
                    random ^= random >> 17;
                    random ^= random << 5;
                    id = new PredictedObjectID(random);
                    Assert.That(actual.Add(id), Is.EqualTo(expected.Add(id)));
                    Assert.That(actual.Contains(new PredictedObjectID(random ^ 0x12345678)),
                        Is.EqualTo(expected.Contains(new PredictedObjectID(random ^ 0x12345678))));
                }
                Assert.That(actual.Count, Is.EqualTo(expected.Count));
                Assert.That(new HashSet<PredictedObjectID>(actual).SetEquals(expected), Is.True);
                foreach (var id in expected)
                    Assert.That(actual.Contains(id), Is.True);
                actual.Clear();
                expected.Clear();
                Assert.That(actual.Count, Is.Zero);
                Assert.That(actual.Contains(default), Is.False);
                Assert.That(actual.Contains(new PredictedObjectID(uint.MaxValue)), Is.False);
            }
        }
    }
}
