#if !NET
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bonsai.Zarr
{
    static class NativeMethods
    {
        public const int MoveFileReplaceExisting = 0x1;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern unsafe bool WriteFile(SafeFileHandle file, byte* buffer, int numberOfBytesToWrite, out int numberOfBytesWritten, IntPtr overlapped);

        public static unsafe void WriteFile(SafeFileHandle file, ReadOnlySpan<byte> data)
        {
            fixed (byte* pointer = data)
            {
                var offset = 0;
                while (offset < data.Length)
                {
                    if (!WriteFile(file, pointer + offset, data.Length - offset, out var written, IntPtr.Zero))
                    {
                        var error = Marshal.GetLastWin32Error();
                        throw new IOException(new Win32Exception(error).Message, error);
                    }

                    offset += written;
                }
            }
        }
    }
}
#endif
