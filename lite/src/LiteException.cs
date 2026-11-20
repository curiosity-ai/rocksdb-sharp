using System;

namespace RocksDbSharp.Lite
{
    public class LiteException : Exception
    {
        public LiteException(string message) : base(message) { }
        public LiteException(string message, Exception inner) : base(message, inner) { }
    }
}
