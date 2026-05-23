using System;
using System.Buffers.Binary;
using System.Text;

namespace RocksDbSharp.Lite
{
    /// <summary>
    /// Encodes scalar values into byte sequences whose lexicographic order matches their natural order.
    /// Encoding choices are tuned for RocksDB's default <see cref="BinaryComparer"/>:
    /// fixed-width big-endian for numerics with sign-bit flip, raw UTF-8 (with 0x00 escaping) for strings.
    ///
    /// Each scalar is encoded with a one-byte type tag prefix so that mixed-type indexes still compare deterministically.
    /// Tags are ordered Null &lt; False &lt; True &lt; Long &lt; Double &lt; DateTime &lt; Guid &lt; String &lt; Bytes.
    /// </summary>
    public static class LiteKey
    {
        internal const byte TagNull     = 0x10;
        internal const byte TagFalse    = 0x20;
        internal const byte TagTrue     = 0x21;
        internal const byte TagLong     = 0x30;
        internal const byte TagDouble   = 0x40;
        internal const byte TagDateTime = 0x50;
        internal const byte TagGuid     = 0x60;
        internal const byte TagString   = 0x70;
        internal const byte TagBytes    = 0x80;

        /// <summary>
        /// Encodes a scalar value into a byte sequence suitable for use as (a component of) a RocksDB key.
        /// Strings cannot contain a literal NUL (0x00) byte because NUL is used as the terminator.
        /// Unsupported types fall back to <see cref="object.ToString"/> encoded as a string.
        /// </summary>
        public static byte[] Encode(object? value)
        {
            if (value is null) return new[] { TagNull };

            switch (value)
            {
                case bool b:
                    return new[] { b ? TagTrue : TagFalse };

                case byte u8:    return EncodeLong(u8);
                case sbyte i8:   return EncodeLong(i8);
                case short i16:  return EncodeLong(i16);
                case ushort u16: return EncodeLong(u16);
                case int i32:    return EncodeLong(i32);
                case uint u32:   return EncodeLong(u32);
                case long i64:   return EncodeLong(i64);
                case ulong u64:  return EncodeLong(checked((long)u64));

                case float f:    return EncodeDouble(f);
                case double d:   return EncodeDouble(d);

                case DateTime dt: return EncodeDateTime(dt);
                case DateTimeOffset dto: return EncodeDateTime(dto.UtcDateTime);

                case Guid g: return EncodeGuid(g);

                case string s: return EncodeString(s);

                case byte[] bytes: return EncodeBytes(bytes);

                default:
                    if (value is IFormattable fmt)
                        return EncodeString(fmt.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
                    return EncodeString(value.ToString() ?? string.Empty);
            }
        }

        private static byte[] EncodeLong(long v)
        {
            // sign-bit flip so that negative numbers sort before positive numbers in unsigned-lex order
            var u = unchecked((ulong)v) ^ 0x8000_0000_0000_0000UL;
            var buf = new byte[9];
            buf[0] = TagLong;
            BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(1), u);
            return buf;
        }

        private static byte[] EncodeDouble(double v)
        {
            // ieee-754 trick: flip sign bit if positive, flip all bits if negative -> lex sorts correctly
            ulong bits = (ulong)BitConverter.DoubleToInt64Bits(v);
            ulong mask = (bits & 0x8000_0000_0000_0000UL) != 0 ? 0xFFFF_FFFF_FFFF_FFFFUL : 0x8000_0000_0000_0000UL;
            bits ^= mask;
            var buf = new byte[9];
            buf[0] = TagDouble;
            BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(1), bits);
            return buf;
        }

        private static byte[] EncodeDateTime(DateTime v)
        {
            var u = unchecked((ulong)v.Ticks) ^ 0x8000_0000_0000_0000UL;
            var buf = new byte[9];
            buf[0] = TagDateTime;
            BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(1), u);
            return buf;
        }

        private static byte[] EncodeGuid(Guid v)
        {
            var buf = new byte[17];
            buf[0] = TagGuid;
            v.TryWriteBytes(buf.AsSpan(1));
            return buf;
        }

        private static byte[] EncodeString(string v)
        {
            var len = Encoding.UTF8.GetByteCount(v);
            var buf = new byte[1 + len + 1];
            buf[0] = TagString;
            Encoding.UTF8.GetBytes(v, 0, v.Length, buf, 1);
            for (int i = 1; i < 1 + len; i++)
            {
                if (buf[i] == 0)
                    throw new LiteException("String index/id values cannot contain a literal NUL (0x00) byte.");
            }
            buf[buf.Length - 1] = 0;
            return buf;
        }

        private static byte[] EncodeBytes(byte[] v)
        {
            // length-prefixed; raw bytes can include any value including 0x00
            var buf = new byte[1 + 4 + v.Length];
            buf[0] = TagBytes;
            BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(1, 4), v.Length);
            Buffer.BlockCopy(v, 0, buf, 5, v.Length);
            return buf;
        }

        /// <summary>
        /// Concatenates two key components. Used to form index keys: [encodedIndexValue][encodedDocumentId].
        /// </summary>
        internal static byte[] Concat(byte[] left, byte[] right)
        {
            var buf = new byte[left.Length + right.Length];
            Buffer.BlockCopy(left, 0, buf, 0, left.Length);
            Buffer.BlockCopy(right, 0, buf, left.Length, right.Length);
            return buf;
        }
    }
}
