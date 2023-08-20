using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Linq;

namespace RocksDbSharp
{
    public class RocksSafePath : IDisposable
    {
        public IntPtr Handle { get; private set; }

        public RocksSafePath(string path)
        {
            var enc = new System.Text.UTF8Encoding(false, false);
            byte[] utf16  = enc.GetBytes(path);
            Handle = Marshal.AllocHGlobal(utf16.Length + 1);
            Marshal.Copy(utf16, 0, Handle, utf16.Length);
            Marshal.WriteByte(Handle, utf16.Length, 0); //Add the null-terminator to the byte sequence
        }

        public void Dispose()
        {
            //Disabled disposing, as it seems RocksDB actually save some of these strings without copying
            //This should be tied to the lifetime of the RocksDB object
            //if(Handle != IntPtr.Zero)
            //{
                //Marshal.FreeHGlobal(Handle);
                //Handle = IntPtr.Zero;
            //}
        }
    }
}
