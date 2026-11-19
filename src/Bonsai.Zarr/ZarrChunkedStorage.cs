using System;

namespace Bonsai.Zarr
{
    /// <summary>
    /// Represents the storage of an array chunked only along its first dimension, which streams
    /// uncompressed rows directly into a sequence of chunk files that each hold a fixed number of
    /// rows, or into a single file that holds every row.
    /// </summary>
    /// <remarks>
    /// Each chunk file holds the rows in the same byte order as a raw binary file of the row
    /// elements. With a fixed number of rows per chunk, the last chunk file is padded with the fill
    /// value when the storage is closed.
    /// </remarks>
    internal sealed class ZarrChunkedStorage : IDisposable
    {
        readonly string arrayDirectory;
        readonly int rowRank;
        readonly int? chunkLength;
        readonly byte[] fillRow;
        ZarrChunkStream? chunk;
        long chunkIndex;
        long chunkRows;

        /// <summary>
        /// Initializes a new instance of the <see cref="ZarrChunkedStorage"/> class.
        /// </summary>
        /// <param name="arrayDirectory">The directory of the array.</param>
        /// <param name="rowRank">The number of dimensions of a single row.</param>
        /// <param name="chunkLength">
        /// The number of rows in each chunk, or <see langword="null"/> to store every row in a
        /// single chunk.
        /// </param>
        /// <param name="fillRow">The bytes of a row in which every element is the fill value.</param>
        public ZarrChunkedStorage(string arrayDirectory, int rowRank, int? chunkLength, byte[] fillRow)
        {
            this.arrayDirectory = arrayDirectory;
            this.rowRank = rowRank;
            this.chunkLength = chunkLength;
            this.fillRow = fillRow;
        }

        /// <summary>
        /// Appends whole rows to the array and starts a new chunk file whenever the current chunk is
        /// full.
        /// </summary>
        /// <param name="rows">The little-endian bytes of the row elements in row-major order.</param>
        public void Write(ReadOnlySpan<byte> rows)
        {
            var rowSize = fillRow.Length;
            while (rows.Length > 0)
            {
                chunk ??= new ZarrChunkStream(ZarrStore.GetChunkPath(arrayDirectory, chunkIndex, rowRank), rowSize);
                var count = rows.Length / rowSize;
                if (chunkLength.HasValue)
                {
                    count = (int)Math.Min(count, chunkLength.GetValueOrDefault() - chunkRows);
                }

                chunk.Write(rows.Slice(0, count * rowSize));
                rows = rows.Slice(count * rowSize);
                chunkRows += count;
                if (chunkRows == chunkLength)
                {
                    CompleteChunk(flushToDisk: false);
                }
            }
        }

        /// <summary>
        /// Writes any buffered rows to the current chunk file.
        /// </summary>
        public void Flush()
        {
            chunk?.Flush();
        }

        void CompleteChunk(bool flushToDisk)
        {
            var completed = chunk!;
            chunk = null;
            completed.Finish(flushToDisk);
            chunkIndex++;
            chunkRows = 0;
        }

        /// <summary>
        /// Pads the last chunk with the fill value, if each chunk holds a fixed number of rows, and
        /// flushes it to the storage device.
        /// </summary>
        public void Close()
        {
            if (chunk != null)
            {
                if (chunkLength.HasValue)
                {
                    chunk.WriteRepeated(fillRow, chunkLength.GetValueOrDefault() - chunkRows);
                }

                CompleteChunk(flushToDisk: true);
            }
        }

        /// <summary>
        /// Releases any open chunk file without completing it.
        /// </summary>
        public void Dispose()
        {
            chunk?.Dispose();
        }
    }
}
