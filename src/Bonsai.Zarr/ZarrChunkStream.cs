using System;
using System.IO;

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
        readonly byte[] buffer;
        int bufferLength;

        /// <summary>
        /// Initializes a new instance of the <see cref="ZarrChunkStream"/> class and creates or
        /// replaces the file at the specified path, together with any missing parent directories.
        /// </summary>
        /// <param name="path">The path of the file to write.</param>
        /// <param name="rowSize">
        /// The size of each row, in bytes. The stream writes whole rows to the file, so the file
        /// always ends on a row boundary.
        /// </param>
        public ZarrChunkStream(string path, int rowSize)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 1);
            buffer = new byte[Math.Max(rowSize, TargetBufferSize - TargetBufferSize % rowSize)];
        }

        /// <summary>
        /// Writes a sequence of bytes to the stream.
        /// </summary>
        /// <param name="data">The bytes to write.</param>
        public void Write(ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                var count = Math.Min(data.Length, buffer.Length - bufferLength);
                data.Slice(0, count).CopyTo(buffer.AsSpan(bufferLength));
                bufferLength += count;
                data = data.Slice(count);
                if (bufferLength == buffer.Length)
                {
                    WriteBuffer();
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

        void WriteBuffer()
        {
            stream.Write(buffer, 0, bufferLength);
            bufferLength = 0;
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
            WriteBuffer();
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
