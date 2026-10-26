using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class ContentIndexTests
    {
        [Test]
        public void LookupsMatchADictionaryAcrossTicksAndGrowth([Range(1, 4)] int seed)
        {
            var random = new Random(seed);
            var index = new ContentIndex();
            var expected = new Dictionary<ulong, int>();
            for (int tick = 0; tick < 300; tick++)
            {
                index.Clear();
                expected.Clear();
                int entries = random.Next(0, tick % 50 == 0 ? 3000 : 600);
                for (int i = 0; i < entries; i++)
                {
                    // Few distinct keys repeat like identical inputs; some collide in the low bits.
                    int shape = random.Next(3);
                    ulong key = shape == 0 ? (ulong)random.Next(64)
                        : shape == 1 ? (ulong)random.Next(8) << 40
                        : (ulong)random.Next() << 32 | (uint)random.Next();
                    Assert.That(index.TryGetValue(key, out int value), Is.EqualTo(expected.TryGetValue(key, out int want)));
                    Assert.That(value, Is.EqualTo(want));
                    index.Set(key, i);
                    expected[key] = i;
                }
                Assert.That(index.Count, Is.EqualTo(expected.Count));
            }
        }
    }
}
