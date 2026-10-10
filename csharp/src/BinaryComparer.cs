using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;

namespace RocksDbSharp
{
    // TODO: consider somehow reusing the actual unmanaged comparer
    public class BinaryComparer : IEqualityComparer<byte[]>, IComparer<byte[]>
    {
        public static BinaryComparer Default { get; } = new BinaryComparer();

        public int Compare(byte[] a1, byte[] a2)
        {
#if NETSTANDARD2_0
            int length = Math.Min(a1.Length, a2.Length);
            for (int i = 0; i < length; i++)
            {
                if (a1[i] != a2[i]) return a1[i] < a2[i] ? -1 : 1;
            }
            return a1.Length == a2.Length ? 0 : (a1.Length < a2.Length ? -1 : 1);
#else
            return Math.Sign(a1.AsSpan().SequenceCompareTo(a2));
#endif
        }

        public bool Equals(byte[] a1, byte[] a2)
        {
            if (ReferenceEquals(a1, a2))
                return true;
            if (a1 == null || a2 == null || a1.Length != a2.Length)
                return false;
            return BytesEqual(a1, a2, a1.Length);
        }

        public bool PrefixEquals(byte[] a1, byte[] a2, int prefix)
        {
            if (ReferenceEquals(a1, a2))
                return true;
            prefix = Math.Min(prefix, Math.Max(a1.Length, a2.Length));
            var a1length = Math.Min(prefix, a1.Length);
            var a2length = Math.Min(prefix, a2.Length);
            if (a1 == null || a2 == null || a1length != a2length)
                return false;
            return BytesEqual(a1, a2, a1length);
        }

        static bool BytesEqual(byte[] a1, byte[] a2, int length)
        {
#if NETSTANDARD2_0
            for (int i = 0; i < length; i++)
            {
                if (a1[i] != a2[i]) return false;
            }
            return true;
#else
            return a1.AsSpan(0, length).SequenceEqual(a2.AsSpan(0, length));
#endif
        }

        public int GetHashCode(byte[] obj)
        {
            return MurMurHash3.Hash(new MemoryStream(obj));
        }
    }

    /*
    This code is public domain.

    The MurmurHash3 algorithm was created by Austin Appleby and put into the public domain.  See http://code.google.com/p/smhasher/

    This C# variant was authored by
    Elliott B. Edwards and was placed into the public domain as a gist
    Status...Working on verification (Test Suite)
    Set up to run as a LinqPad (linqpad.net) script (thus the ".Dump()" call)
    */
    public static class MurMurHash3
    {
        //Change to suit your needs
        const uint seed = 144;

        // TODO: optimize this to use a byte pointer
        public static int Hash(Stream stream)
        {
            const uint c1 = 0xcc9e2d51;
            const uint c2 = 0x1b873593;

            uint h1 = seed;
            uint k1 = 0;
            uint streamLength = 0;

            using (BinaryReader reader = new BinaryReader(stream))
            {
                byte[] chunk = reader.ReadBytes(4);
                while (chunk.Length > 0)
                {
                    streamLength += (uint)chunk.Length;
                    switch (chunk.Length)
                    {
                        case 4:
                            /* Get four bytes from the input into an uint */
                            k1 = (uint)
                                (chunk[0]
                                    | chunk[1] << 8
                                    | chunk[2] << 16
                                    | chunk[3] << 24);

                            /* bitmagic hash */
                            k1 *= c1;
                            k1 = Rotl32(k1, 15);
                            k1 *= c2;

                            h1 ^= k1;
                            h1 = Rotl32(h1, 13);
                            h1 = h1 * 5 + 0xe6546b64;
                            break;
                        case 3:
                            k1 = (uint)
                                (chunk[0]
                                    | chunk[1] << 8
                                    | chunk[2] << 16);
                            k1 *= c1;
                            k1 = Rotl32(k1, 15);
                            k1 *= c2;
                            h1 ^= k1;
                            break;
                        case 2:
                            k1 = (uint)
                                (chunk[0]
                                    | chunk[1] << 8);
                            k1 *= c1;
                            k1 = Rotl32(k1, 15);
                            k1 *= c2;
                            h1 ^= k1;
                            break;
                        case 1:
                            k1 = (uint)(chunk[0]);
                            k1 *= c1;
                            k1 = Rotl32(k1, 15);
                            k1 *= c2;
                            h1 ^= k1;
                            break;

                    }
                    chunk = reader.ReadBytes(4);
                }
            }

            // finalization, magic chants to wrap it all up
            h1 ^= streamLength;
            h1 = Fmix(h1);

            unchecked //ignore overflow
            {
                return (int)h1;
            }
        }

        private static uint Rotl32(uint x, byte r)
        {
            return (x << r) | (x >> (32 - r));
        }

        private static uint Fmix(uint h)
        {
            h ^= h >> 16;
            h *= 0x85ebca6b;
            h ^= h >> 13;
            h *= 0xc2b2ae35;
            h ^= h >> 16;
            return h;
        }
    }
}

