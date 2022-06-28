using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RocksDbSharp
{
    public class UTF8String : IDisposable
    {
        public IntPtr Handle { get; private set; }

        public UTF8String(string source)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(source);
            Handle = Marshal.AllocHGlobal(utf8.Length);
            Marshal.Copy(utf8, 0, Handle, utf8.Length);
        }

        public void Dispose()
        {
            if(Handle != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Handle);
                Handle = IntPtr.Zero;
            }
        }
    }
}
