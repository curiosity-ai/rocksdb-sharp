using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RocksDbSharp
{
    public class RocksSafePath : IDisposable
    {
        public IntPtr Handle { get; private set; }

        public RocksSafePath(string path)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                byte[] utf16 = Encoding.Unicode.GetBytes(path);
                Handle = Marshal.AllocHGlobal(utf16.Length);
                Marshal.Copy(utf16, 0, Handle, utf16.Length);
            }
            else
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(path);
                Handle = Marshal.AllocHGlobal(utf8.Length);
                Marshal.Copy(utf8, 0, Handle, utf8.Length);
            }
            //Handle = Marshal.StringToHGlobalAnsi(source);
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
