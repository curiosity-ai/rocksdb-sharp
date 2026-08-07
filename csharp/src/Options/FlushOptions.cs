using System;

namespace RocksDbSharp
{
    public class FlushOptions
    {
        public FlushOptions()
        {
            Handle = Native.Instance.rocksdb_flushoptions_create();
        }

        public IntPtr Handle { get; protected set; }

        ~FlushOptions()
        {
            if (Handle != IntPtr.Zero)
            {
#if !NODESTROY
                Native.Instance.rocksdb_flushoptions_destroy(Handle);
#endif
                Handle = IntPtr.Zero;
            }
        }

        public FlushOptions SetWaitForFlush(bool waitForFlush)
        {
            Native.Instance.rocksdb_flushoptions_set_wait(Handle, Native.MarshalBool(waitForFlush));
            return this;
        }
    }
}
