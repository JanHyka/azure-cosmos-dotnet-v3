// ------------------------------------------------------------
// The code in this repository code was written by Lee Campbell, as a
// derived work from the original Java by Gil Tene of Azul Systems and
// Michael Barker, and released to the public domain, as explained
// at http://creativecommons.org/publicdomain/zero/1.0/
// ------------------------------------------------------------

// This file isn't generated, but this comment is necessary to exclude it from StyleCop analysis.
// <auto-generated/>

using System;
using HdrHistogram.Utilities;

namespace HdrHistogram.Persistence
{
    sealed class V2MaxWordSizeCountsDecoder : ICountsDecoder
    {
        // LEB128-64b9B + ZigZag require up to 9 bytes per word
        public int WordSize => 9;

        public int ReadCounts(ByteBuffer sourceBuffer, int lengthInBytes, int maxIndex, Action<int, long> setCount)
        {
            var idx = 0;
            int endPosition = sourceBuffer.Position + lengthInBytes;
            while (sourceBuffer.Position < endPosition && idx < maxIndex)
            {
                var item = ZigZagEncoding.GetLong(sourceBuffer);
                if (item < 0)
                {
                    var zeroCounts = -(item);
                    if (zeroCounts > int.MaxValue)
                    {
                        throw new ArgumentException("An encoded zero count of > int.MaxValue was encountered in the source");
                    }
                    idx += (int)zeroCounts;
                }
                else
                {
                    setCount(idx++, item);
                }
            }
            return idx;
        }
    }
}