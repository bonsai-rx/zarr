using System;
using System.IO;
#if !NET
using Microsoft.Win32.SafeHandles;
#endif

namespace Bonsai.Zarr
{
    /// <summary>
    /// Represents a buffered stream that writes the uncompressed bytes of a chunk directly to its
    /// final key in a local directory store.
    /// </summary>
    internal sealed class ZarrChunkStream : IDisposable
    {
        const int TargetBufferSize = 65536;

        readonly FileStream stream;
#if !NET
        readonly SafeFileHandle handle;
#endif
        readonly byte[] buffer;
        int bufferLength;

        /// <summary>
        /// Initializes a new instance of the <see cref="ZarrChunkStream"/> class and creates or
        /// replaces the file at the specified path, together with any missing parent directories.
        /// </summary>
        /// <param name="path">The path of the file to write.</param>
        /// <param name="rowSize">
        /// The size of each row, in bytes. The stream always writes whole rows to the file.
        /// </param>
        public ZarrChunkStream(string path, int rowSize)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 1);
#if !NET
            handle = stream.SafeFileHandle;
#endif
            buffer = new byte[Math.Max(rowSize, TargetBufferSize - TargetBufferSize % rowSize)];
        }

        /// <summary>
        /// Writes whole rows to the stream. Rows that do not fill the buffer are kept in it until
        /// the buffer is full or the stream is flushed.
        /// </summary>
        /// <param name="data">The bytes of the rows to write.</param>
        public void Write(ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                if (bufferLength == 0 && data.Length >= buffer.Length)
                {
                    WriteFile(data);
                    return;
                }

                var count = Math.Min(data.Length, buffer.Length - bufferLength);
                data.Slice(0, count).CopyTo(buffer.AsSpan(bufferLength));
                bufferLength += count;
                data = data.Slice(count);
                if (bufferLength == buffer.Length)
                {
                    Flush();
                }
            }
        }

        /// <summary>
        /// Writes a sequence of bytes to the stream the specified number of times.
        /// </summary>
        /// <param name="pattern">The bytes to write repeatedly.</param>
        /// <param name="count">The number of times to write the bytes.</param>
        public void WriteRepeated(ReadOnlySpan<byte> pattern, long count)
        {
            for (long i = 0; i < count; i++)
            {
                Write(pattern);
            }
        }

        /// <summary>
        /// Writes any buffered rows to the file.
        /// </summary>
        public void Flush()
        {
            if (bufferLength > 0)
            {
                WriteFile(buffer.AsSpan(0, bufferLength));
                bufferLength = 0;
            }
        }

        void WriteFile(ReadOnlySpan<byte> data)
        {
#if NET
            stream.Write(data);
#else
            NativeMethods.WriteFile(handle, data);
#endif
        }

        /// <summary>
        /// Writes any buffered bytes and closes the file.
        /// </summary>
        /// <param name="flushToDisk">
        /// <see langword="true"/> to flush the file contents to the storage device before closing;
        /// otherwise, <see langword="false"/>.
        /// </param>
        public void Finish(bool flushToDisk)
        {
            Flush();
            stream.Flush(flushToDisk);
            stream.Dispose();
        }

        /// <summary>
        /// Closes the file without writing any buffered bytes.
        /// </summary>
        public void Dispose()
        {
            stream.Dispose();
        }
    }
}
