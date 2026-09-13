using System;
using System.Collections.Generic;
using NUnit.Framework;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Prediction.Tests.Editor
{
    public sealed class AuthoritativeStateEqualityTests
    {
        private IEqualityComparer<AuthorityEqualityWireProbe> _previousEquality;
        private static int _writes;

        [SetUp]
        public void SetUp()
        {
            NetworkManager.CallAllRegisters();
            _previousEquality = PurrEquality<AuthorityEqualityWireProbe>.Default;
            PurrEquality<AuthorityEqualityWireProbe>.Default = new CoarseComparer();
            Packer<AuthorityEqualityWireProbe>.RegisterWriter((packer, value) =>
            {
                _writes++;
                for (int i = 0; i < value.bitCount; i++)
                    Packer<bool>.Write(packer, value.oneBits);
            });
            Packer<AuthorityEqualityUnmanagedProbe>.RegisterWriter((packer, value) =>
                throw new InvalidOperationException("unmanaged equality must not serialize"));
            _writes = 0;
        }

        [TearDown]
        public void TearDown()
        {
            PurrEquality<AuthorityEqualityWireProbe>.Default = _previousEquality;
        }

        [Test]
        public void GeneratedApproximateManagedEqualityCannotConfirmDifferentWireBits()
        {
            var left = new DeterministicGeneratedEqualityState { label = "same", position = Vector3.zero };
            var right = new DeterministicGeneratedEqualityState { label = "same", position = new Vector3(0.000001f, 0f, 0f) };
            Assert.That(Packer.AreEqualRef(ref left, ref right), Is.True);
            Assert.That(AuthoritativeStateEquality<DeterministicGeneratedEqualityState>.AreEqual(ref left, ref right), Is.False);
            right = left;
            Assert.That(AuthoritativeStateEquality<DeterministicGeneratedEqualityState>.AreEqual(ref left, ref right), Is.True);
        }

        [Test]
        public void CheapUnequalResultAvoidsSerialization()
        {
            var left = new AuthorityEqualityWireProbe { label = "left", bitCount = 1 };
            var right = new AuthorityEqualityWireProbe { label = "right", bitCount = 1 };
            Assert.That(AuthoritativeStateEquality<AuthorityEqualityWireProbe>.AreEqual(ref left, ref right), Is.False);
            Assert.That(_writes, Is.Zero);
        }

        [Test]
        public void EqualBackingBytePrefixesWithDifferentBitLengthsAreDifferentStates()
        {
            var left = new AuthorityEqualityWireProbe { label = "same", bitCount = 1 };
            var right = new AuthorityEqualityWireProbe { label = "same", bitCount = 2 };
            Assert.That(AuthoritativeStateEquality<AuthorityEqualityWireProbe>.AreEqual(ref left, ref right), Is.False);
            Assert.That(_writes, Is.EqualTo(2));
        }

        [TestCase(1)]
        [TestCase(9)]
        public void EqualNonByteAlignedSerializedStatesRemainEqual(int bitCount)
        {
            var left = new AuthorityEqualityWireProbe { label = "same", bitCount = bitCount, oneBits = true };
            var right = left;
            Assert.That(AuthoritativeStateEquality<AuthorityEqualityWireProbe>.AreEqual(ref left, ref right), Is.True);
            right.oneBits = false;
            Assert.That(AuthoritativeStateEquality<AuthorityEqualityWireProbe>.AreEqual(ref left, ref right), Is.False);
        }

        [Test]
        public void UnmanagedEqualityRetainsItsExactNonSerializingPath()
        {
            var left = new AuthorityEqualityUnmanagedProbe { value = 31 };
            var right = left;
            Assert.That(AuthoritativeStateEquality<AuthorityEqualityUnmanagedProbe>.AreEqual(ref left, ref right), Is.True);
            right.value++;
            Assert.That(AuthoritativeStateEquality<AuthorityEqualityUnmanagedProbe>.AreEqual(ref left, ref right), Is.False);
        }

        private sealed class CoarseComparer : IEqualityComparer<AuthorityEqualityWireProbe>
        {
            public bool Equals(AuthorityEqualityWireProbe left, AuthorityEqualityWireProbe right) => left.label == right.label;
            public int GetHashCode(AuthorityEqualityWireProbe value) => value.label?.GetHashCode() ?? 0;
        }
    }

    public struct AuthorityEqualityWireProbe
    {
        public string label;
        public int bitCount;
        public bool oneBits;
    }
    public struct AuthorityEqualityUnmanagedProbe { public int value; }
}
