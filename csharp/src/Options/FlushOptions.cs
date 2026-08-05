using System;
using System.Collections.Generic;
using System.Text;

namespace RocksDbSharp
{
    public class FlushOptions
    {
        public FlushOptions()
        {
            Handle = Native.Instance.rocksdb_flushoptions_create();
        }

        public IntPtr Handle { get; private set; }

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

        /// <summary>
        /// When true, <c>Flush</c> does not return until the memtables have been flushed.
        /// </summary>
        public FlushOptions SetWaitForFlush(bool waitForFlush)
        {
            Native.Instance.rocksdb_flushoptions_set_wait(Handle, Native.MarshalBool(waitForFlush));
            return this;
        }

        public bool GetWaitForFlush()
            => Native.Instance.rocksdb_flushoptions_get_wait(Handle) != 0;

        /// <summary>
        /// When false (the default), <c>Flush</c> fails rather than waiting if the flush would
        /// have to stall writes to proceed.
        /// </summary>
        public FlushOptions SetAllowWriteStall(bool allowWriteStall)
        {
            Native.Instance.rocksdb_flushoptions_set_allow_write_stall(Handle, Native.MarshalBool(allowWriteStall));
            return this;
        }

        public bool GetAllowWriteStall()
            => Native.Instance.rocksdb_flushoptions_get_allow_write_stall(Handle) != 0;

        /// <summary>
        /// Flush the given column families atomically, as one memtable switch across all of them.
        /// </summary>
        public FlushOptions SetForceAtomicFlush(bool forceAtomicFlush)
        {
            Native.Instance.rocksdb_flushoptions_set_force_atomic_flush(Handle, Native.MarshalBool(forceAtomicFlush));
            return this;
        }

        public bool GetForceAtomicFlush()
            => Native.Instance.rocksdb_flushoptions_get_force_atomic_flush(Handle) != 0;

        /// <summary>
        /// Together with <see cref="SetWaitForFlush"/>, makes <c>Flush</c> wait for the registered
        /// event listeners' OnFlushCompleted callbacks to finish as well, not just for the flush
        /// result to be committed.
        /// </summary>
        /// <remarks>
        /// Defaults to false, which is the behaviour of every release before RocksDB 11.8.0: a
        /// waiting <c>Flush</c> can return while OnFlushCompleted is still running on the
        /// background flush thread. Has no effect unless <see cref="SetWaitForFlush"/> is set.
        /// </remarks>
        public FlushOptions SetWaitForListeners(bool waitForListeners)
        {
            Native.Instance.rocksdb_flushoptions_set_listener_wait(Handle, Native.MarshalBool(waitForListeners));
            return this;
        }

        public bool GetWaitForListeners()
            => Native.Instance.rocksdb_flushoptions_get_listener_wait(Handle) != 0;
    }
}
