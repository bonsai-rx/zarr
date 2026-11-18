#if !NET
using System.Runtime.InteropServices;

namespace Bonsai.Zarr
{
    static class NativeMethods
    {
        public const int MoveFileReplaceExisting = 0x1;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);
    }
}
#endif
