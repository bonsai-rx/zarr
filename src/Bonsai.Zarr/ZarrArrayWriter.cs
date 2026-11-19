using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using OpenCV.Net;

namespace Bonsai.Zarr
{
    /// <summary>
    /// Represents a writer that appends rows of fixed shape along the first dimension of a
    /// Zarr version 3 array in a local directory store.
    /// </summary>
    /// <remarks>
    /// The array metadata is written when the writer is created, with an empty first dimension, and
    /// again when the writer is disposed, with the number of rows appended. Rows appended before
    /// the writer is disposed are stored in chunk files but are not covered by the published shape.
    /// </remarks>
    internal sealed class ZarrArrayWriter : IDisposable
    {
        readonly string metadataPath;
        readonly ZarrDataType dataType;
        readonly int[] rowShape;
        readonly int rowSize;
        readonly int? chunkLength;
        readonly string[] dimensionNames;
        readonly ZarrChunkedStorage storage;
        byte[] transposeBuffer = Array.Empty<byte>();
        bool disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="ZarrArrayWriter"/> class and creates the
        /// array with no rows, together with any missing ancestor groups.
        /// </summary>
        /// <param name="storePath">The directory of the store.</param>
        /// <param name="arrayPath">
        /// The path of the array relative to the store root, with node names separated by slashes.
        /// </param>
        /// <param name="dataType">The data type of the array elements.</param>
        /// <param name="rowShape">
        /// The length of each dimension of a single row. An empty shape creates a one-dimensional array.
        /// </param>
        /// <param name="settings">The chunking and creation options of the array.</param>
        /// <exception cref="IOException">
        /// A node already exists at the specified path and cannot be replaced, or a node along the
        /// path is not a group.
        /// </exception>
        public ZarrArrayWriter(string storePath, string arrayPath, ZarrDataType dataType, int[] rowShape, ZarrArraySettings settings)
        {
            if (string.IsNullOrEmpty(storePath))
            {
                throw new ArgumentException("A store path must be specified.", nameof(storePath));
            }

            if (settings.ChunkLength <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(settings), "The chunk length must be a positive number.");
            }

            if (rowShape.Any(length => length <= 0))
            {
                throw new ArgumentOutOfRangeException(nameof(rowShape), "The length of every row dimension must be a positive number.");
            }

            if (!BitConverter.IsLittleEndian)
            {
                throw new PlatformNotSupportedException("Writing Zarr arrays requires a little-endian platform.");
            }

            var nodeNames = ZarrStore.GetNodeNames(arrayPath);
            var arrayName = nodeNames[nodeNames.Length - 1];
            var rank = rowShape.Length + 1;
            dimensionNames = settings.DimensionNames ?? Enumerable.Range(0, rank).Select(i => $"{arrayName}_dim_{i}").ToArray();
            if (dimensionNames.Length != rank)
            {
                throw new ArgumentException($"The number of dimension names must be equal to the array rank {rank}.", nameof(settings));
            }

            this.dataType = dataType;
            this.rowShape = (int[])rowShape.Clone();
            RowLength = rowShape.Aggregate(1, (product, length) => checked(product * length));
            rowSize = checked(RowLength * dataType.ElementSize);
            chunkLength = settings.ChunkLength;

            var groupDirectory = ZarrStore.EnsureGroups(storePath, nodeNames.Take(nodeNames.Length - 1));
            var arrayDirectory = Path.Combine(groupDirectory, arrayName);
            ZarrStore.PrepareNode(arrayDirectory, settings.Overwrite);
            Directory.CreateDirectory(arrayDirectory);
            metadataPath = Path.Combine(arrayDirectory, ZarrStore.MetadataKey);
            storage = new ZarrChunkedStorage(arrayDirectory, rowShape.Length, chunkLength, new byte[rowSize]);
            try
            {
                WriteMetadata(0);
            }
            catch
            {
                storage.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Gets the data type of the array elements.
        /// </summary>
        public ZarrDataType DataType => dataType;

        /// <summary>
        /// Gets the number of elements in each row.
        /// </summary>
        public int RowLength { get; }

        /// <summary>
        /// Gets the number of rows appended to the array.
        /// </summary>
        public long RowCount { get; private set; }

        /// <summary>
        /// Appends whole rows to the array.
        /// </summary>
        /// <param name="rows">The little-endian bytes of the row elements in row-major order.</param>
        /// <exception cref="ArgumentException">
        /// The number of bytes is not a multiple of the size of a row.
        /// </exception>
        public void Write(ReadOnlySpan<byte> rows)
        {
            if (rows.Length % rowSize != 0)
            {
                throw new ArgumentException(
                    $"The data has {rows.Length} bytes, which is not a whole number of rows of {rowSize} bytes.",
                    nameof(rows));
            }

            EnsureNotDisposed();
            storage.Write(rows);
            RowCount += rows.Length / rowSize;
        }

        /// <summary>
        /// Appends each column of a single-channel matrix as a new row of the array.
        /// </summary>
        /// <param name="matrix">
        /// The matrix to write, whose number of rows must equal the row length and whose elements
        /// must have the data type of the array.
        /// </param>
        /// <exception cref="ArgumentException">
        /// The number of matrix rows differs from the number of elements in each array row.
        /// </exception>
        public void WriteColumns(Mat matrix)
        {
            if (matrix.Rows != RowLength)
            {
                throw new ArgumentException(
                    $"The matrix has {matrix.Rows} rows but the array expects {RowLength} elements per row.",
                    nameof(matrix));
            }

            EnsureNotDisposed();
            var length = checked(matrix.Cols * rowSize);
            if (transposeBuffer.Length < length)
            {
                transposeBuffer = new byte[length];
            }

            var handle = GCHandle.Alloc(transposeBuffer, GCHandleType.Pinned);
            try
            {
                using var transposed = new Mat(matrix.Cols, matrix.Rows, matrix.Depth, 1, handle.AddrOfPinnedObject(), rowSize);
                CV.Transpose(matrix, transposed);
            }
            finally
            {
                handle.Free();
            }

            storage.Write(transposeBuffer.AsSpan(0, length));
            RowCount += matrix.Cols;
        }

        /// <summary>
        /// Writes any buffered rows to the current chunk file.
        /// </summary>
        public void Flush()
        {
            EnsureNotDisposed();
            storage.Flush();
        }

        void WriteMetadata(long rowCount)
        {
            var document = ZarrMetadata.CreateArray(
                shape: new[] { rowCount }.Concat(rowShape.Select(length => (long)length)),
                dataType.Name,
                chunkShape: new[] { chunkLength ?? Math.Max(rowCount, 1) }.Concat(rowShape.Select(length => (long)length)),
                dataType.FillValue,
                ZarrMetadata.CreateBytesCodec(dataType.ElementSize),
                dimensionNames);
            ZarrStore.WriteMetadata(metadataPath, document);
        }

        void EnsureNotDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(ZarrArrayWriter));
            }
        }

        /// <summary>
        /// Pads the last chunk with the fill value, updates the array shape to cover every
        /// appended row, and releases the resources used by the writer.
        /// </summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                storage.Close();
                WriteMetadata(RowCount);
            }
            finally
            {
                storage.Dispose();
            }
        }
    }
}
