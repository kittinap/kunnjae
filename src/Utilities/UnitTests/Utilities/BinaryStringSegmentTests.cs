﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Text;
using BuildXL.Utilities;
using Test.BuildXL.TestUtilities.Xunit;
using Xunit;

namespace Test.BuildXL.Utilities
{
    public sealed class BinaryStringSegmentTests
    {
        public BinaryStringSegment CreateSegment(string value, bool isAscii)
        {
            byte[] bytes = isAscii ?
                Encoding.ASCII.GetBytes(value) :
                Encoding.Unicode.GetBytes(value);

            // Unfortunately the byte interpretation for ICharSpan/StringTable do not
            // match what is produced by the encoding. Namely Encoding writes bytes little endian
            // while we use big endian. The code below switches the encoding generated bytes to big
            // endian.
            if (!isAscii)
            {
                for (int i = 0; i < bytes.Length; i += 2)
                {
                    var tmp = bytes[i];
                    bytes[i] = bytes[i + 1];
                    bytes[i + 1] = tmp;
                }
            }

            return new BinaryStringSegment(bytes, 0, bytes.Length, isAscii);
        }

        public BinaryStringSegment CreateSegment(string value, int startIndex, int length, bool isAscii)
        {
            return CreateSegment(value, isAscii).Subsegment(startIndex, length);
        }

        [Fact]
        public void General()
        {
            RunForEncoding(isAscii =>
            {
                var seg = CreateSegment(string.Empty, isAscii);
                XAssert.AreEqual(0, seg.Length);

                seg = CreateSegment("A", isAscii);
                XAssert.AreEqual(1, seg.Length);
                XAssert.AreEqual('A', seg[0]);

                if (!isAscii)
                {
                    seg = CreateSegment("繙", isAscii);
                    XAssert.AreEqual(1, seg.Length);
                    XAssert.AreEqual('繙', seg[0]);
                }

                var stable = CreateSegment("AB", isAscii);

                seg = CreateSegment("AB", isAscii);
                XAssert.AreEqual(2, seg.Length);
                XAssert.AreEqual('A', seg[0]);
                XAssert.AreEqual('B', seg[1]);
                XAssert.AreEqual(stable, seg);

                if (!isAscii)
                {
                    seg = CreateSegment("繙B", isAscii);
                    XAssert.AreEqual(2, seg.Length);
                    XAssert.AreEqual('繙', seg[0]);
                    XAssert.AreEqual('B', seg[1]);
                    var stableSeg = stable.Subsegment(1, 1);
                    var lastSeg = seg.Subsegment(1, 1);
                    XAssert.AreEqual(stableSeg, lastSeg);
                }

                seg = CreateSegment("XABY", 1, 2, isAscii);
                XAssert.AreEqual(2, seg.Length);
                XAssert.AreEqual('A', seg[0]);
                XAssert.AreEqual('B', seg[1]);
                XAssert.AreEqual(stable, seg);

                seg = CreateSegment("ABY", 0, 2, isAscii);
                XAssert.AreEqual(2, seg.Length);
                XAssert.AreEqual('A', seg[0]);
                XAssert.AreEqual('B', seg[1]);
                XAssert.AreEqual(stable, seg);

                seg = CreateSegment("XAB", 1, 2, isAscii);
                XAssert.AreEqual(2, seg.Length);
                XAssert.AreEqual('A', seg[0]);
                XAssert.AreEqual('B', seg[1]);
                XAssert.AreEqual(stable, seg);

                VerifyStringExhaustive("Hello this is a string", isAscii);
            });
        }

        [Fact]
        public void VerifyExhaustive()
        {
            RunForEncoding(isAscii =>
            {
                VerifyStringExhaustive("Hello this is a string", isAscii);

                if (!isAscii)
                {
                    VerifyStringExhaustive("this 繙 is a unicode string", isAscii);
                }
            });
        }

        [Fact]
        public void Empty()
        {
            RunForEncoding(isAscii =>
            {
                var seg = StringSegment.Empty;
                XAssert.AreEqual(0, seg.Length);
                XAssert.IsTrue(seg.IndexOf("AB") < 0);
                XAssert.AreEqual(seg, seg);

                XAssert.AreNotEqual(CreateSegment("ABC", isAscii), seg);
            });
        }

        [Fact]
        public void Subsegment()
        {
            RunForEncoding(isAscii =>
            {
                var seg = CreateSegment(string.Empty, isAscii);
                var sub = seg.Subsegment(0, 0);
                XAssert.AreEqual(0, sub.Length);

                seg = CreateSegment("ABCDEF", isAscii);
                sub = seg.Subsegment(0, 1);
                XAssert.AreEqual(1, sub.Length);
                XAssert.AreEqual('A', sub[0]);

                sub = seg.Subsegment(5, 1);
                XAssert.AreEqual(1, sub.Length);
                XAssert.AreEqual('F', sub[0]);

                sub = seg.Subsegment(2, 3);
                XAssert.AreEqual(3, sub.Length);
                XAssert.AreEqual('C', sub[0]);
                XAssert.AreEqual('D', sub[1]);
                XAssert.AreEqual('E', sub[2]);
            });
        }

        [Fact]
        public void IsEqual()
        {
            RunForEncoding(isAscii =>
            {
                var s1 = CreateSegment("ABCDEF", isAscii);
                var s2 = CreateSegment("ABCDEF", 0, 6, isAscii);
                XAssert.IsTrue(s1.Equals(s2));
                XAssert.IsTrue(s1 == s2);
                XAssert.IsFalse(s1 != s2);

                s2 = CreateSegment("XABCDEF", 1, 6, isAscii);
                XAssert.IsTrue(s1.Equals(s2));
                XAssert.IsTrue(s1 == s2);
                XAssert.IsFalse(s1 != s2);

                s2 = CreateSegment("ABCDEF", 0, 5, isAscii);
                XAssert.IsFalse(s1.Equals(s2));
                XAssert.IsFalse(s1 == s2);
                XAssert.IsTrue(s1 != s2);

                s2 = CreateSegment("GHIJKL", 0, 6, isAscii);
                XAssert.IsFalse(s1.Equals(s2));
                XAssert.IsFalse(s1 == s2);
                XAssert.IsTrue(s1 != s2);
            });
        }

        private void RunForEncoding(Action<bool> action)
        {
            action(true);
            action(false);
        }

        private void VerifyStringExhaustive(string value, bool isAscii)
        {
            var otherString = "TEST{" + value + "}SURROUND";
            var otherStringSegment = CreateSegment(otherString, isAscii).Subsegment(5, value.Length);

            var fullSegment = CreateSegment(value, isAscii);
            var otherFullSegment = CreateSegment(value, isAscii);
            BinaryStringSegment fullUnicodeSegment = default(BinaryStringSegment);
            if (isAscii)
            {
                // Create a unicode version to compare to.
                fullUnicodeSegment = CreateSegment(value, isAscii: false);
                VerifyEquals(fullSegment, fullUnicodeSegment);
                VerifyEquals(otherStringSegment, fullUnicodeSegment);
            }

            VerifyEquals(fullSegment, fullSegment);
            VerifyEquals(fullSegment, otherFullSegment);

            for (int startIndex = 0; startIndex < value.Length; startIndex++)
            {
                XAssert.AreEqual(value[startIndex], fullSegment[startIndex]);
                XAssert.AreEqual(value[startIndex], otherFullSegment[startIndex]);

                for (int length = startIndex; length < value.Length - startIndex; length++)
                {
                    var fullPartialSegment = CreateSegment(value.Substring(startIndex, length), isAscii);
                    var partialSegment = fullSegment.Subsegment(startIndex, length);
                    var otherPartialSegment = otherStringSegment.Subsegment(startIndex, length);
                    VerifyEquals(partialSegment, fullPartialSegment);
                    VerifyEquals(otherPartialSegment, fullPartialSegment);
                }
            }

            if (isAscii)
            {
                // Verify the Unicode binary segment for the ascii-only character string
                for (int startIndex = 0; startIndex < value.Length; startIndex++)
                {
                    XAssert.AreEqual(value[startIndex], fullUnicodeSegment[startIndex]);

                    for (int length = startIndex; length < value.Length - startIndex; length++)
                    {
                        var fullPartialSegment = CreateSegment(value.Substring(startIndex, length), isAscii);
                        var partialSegment = CreateSegment(value, isAscii).Subsegment(startIndex, length);
                        VerifyEquals(partialSegment, fullPartialSegment);
                    }
                }
            }
        }

        private void VerifyEquals(BinaryStringSegment s1, BinaryStringSegment s2)
        {
            XAssert.IsTrue(s1.Equals(s2));
            XAssert.IsTrue(s2.Equals(s1));
            XAssert.IsTrue(s2 == s1);
            XAssert.IsTrue(s1 == s2);
            XAssert.IsFalse(s2 != s1);
            XAssert.IsFalse(s1 != s2);
        }
    }
}
