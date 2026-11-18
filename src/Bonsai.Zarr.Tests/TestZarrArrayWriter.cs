using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bonsai.Zarr.Tests
{
    [TestClass]
    public class TestZarrArrayWriter
    {
        const string ArrayName = "data";
        string storePath = string.Empty;

        [TestInitialize]
        public void Initialize()
        {
            storePath = ZarrTestHelper.CreateStorePath();
        }

        [TestCleanup]
        public void Cleanup()
        {
            ZarrTestHelper.DeleteStore(storePath);
        }

        string ArrayPath => Path.Combine(storePath, ArrayName);

        [TestMethod]
        public void Shape_PublishedOnlyOnDisposal()
        {
            // Rows reach their chunk files as they are written, but the shape stays empty until
            // the writer is disposed, so an interrupted array reads as empty until it is repaired.
            using (var writer = ZarrTestHelper.CreateArrayWriter<int>(storePath, ArrayName, Array.Empty<int>(), ZarrTestHelper.CreateSettings(chunkLength: 2)))
            {
                CollectionAssert.AreEqual(new[] { 0L }, ZarrTestHelper.GetShape(ArrayPath));
                Assert.IsFalse(Directory.Exists(Path.Combine(ArrayPath, "c")));

                ZarrTestHelper.WriteRows(writer, 1, 2, 3);
                CollectionAssert.AreEqual(new[] { 0L }, ZarrTestHelper.GetShape(ArrayPath));
                CollectionAssert.AreEqual(new[] { 1, 2 }, ZarrTestHelper.ReadElements<int>(Path.Combine(ArrayPath, "c", "0")));
                Assert.IsTrue(File.Exists(Path.Combine(ArrayPath, "c", "1")));
            }

            CollectionAssert.AreEqual(new[] { 3L }, ZarrTestHelper.GetShape(ArrayPath));
            CollectionAssert.AreEqual(new[] { 3, 0 }, ZarrTestHelper.ReadElements<int>(Path.Combine(ArrayPath, "c", "1")));
        }

        [TestMethod]
        public void ChunkFiles_MatchRawRowBytes()
        {
            // Complete chunk files hold the rows exactly as a raw binary writer would append them.
            var rows = Enumerable.Range(0, 10).Select(row => Enumerable.Range(row * 3, 3).Select(i => (double)i).ToArray()).ToArray();
            using (var writer = ZarrTestHelper.CreateArrayWriter<double>(storePath, ArrayName, new[] { 3 }, ZarrTestHelper.CreateSettings(chunkLength: 4)))
            {
                foreach (var row in rows)
                {
                    ZarrTestHelper.WriteRows(writer, row);
                }
            }

            var raw = rows.SelectMany(row => row.SelectMany(BitConverter.GetBytes)).ToArray();
            var stored = Enumerable.Range(0, 3)
                .SelectMany(chunk => File.ReadAllBytes(Path.Combine(ArrayPath, "c", chunk.ToString(), "0")))
                .ToArray();
            Assert.HasCount(12 * 3 * sizeof(double), stored);
            CollectionAssert.AreEqual(raw, stored.Take(raw.Length).ToArray());
            CollectionAssert.AreEqual(new byte[2 * 3 * sizeof(double)], stored.Skip(raw.Length).ToArray());
        }

        [TestMethod]
        public void RowsSpanningChunks_SplitAcrossChunkFiles()
        {
            using (var writer = ZarrTestHelper.CreateArrayWriter<short>(storePath, ArrayName, Array.Empty<int>(), ZarrTestHelper.CreateSettings(chunkLength: 4)))
            {
                ZarrTestHelper.WriteRows<short>(writer, 1, 2, 3);
                ZarrTestHelper.WriteRows<short>(writer, 4, 5, 6, 7, 8, 9, 10);
            }

            CollectionAssert.AreEqual(new[] { 10L }, ZarrTestHelper.GetShape(ArrayPath));
            CollectionAssert.AreEqual(new short[] { 1, 2, 3, 4 }, ZarrTestHelper.ReadElements<short>(Path.Combine(ArrayPath, "c", "0")));
            CollectionAssert.AreEqual(new short[] { 5, 6, 7, 8 }, ZarrTestHelper.ReadElements<short>(Path.Combine(ArrayPath, "c", "1")));
            CollectionAssert.AreEqual(new short[] { 9, 10, 0, 0 }, ZarrTestHelper.ReadElements<short>(Path.Combine(ArrayPath, "c", "2")));
        }

        [TestMethod]
        public void WriteColumns_WritesOneRowPerColumn()
        {
            // A row-major matrix with one row per channel is stored with one row per sample.
            var matrix = new short[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            var handle = GCHandle.Alloc(matrix, GCHandleType.Pinned);
            try
            {
                using var writer = ZarrTestHelper.CreateArrayWriter<short>(storePath, ArrayName, new[] { 2 }, ZarrTestHelper.CreateSettings(chunkLength: 4));
                writer.WriteColumns(handle.AddrOfPinnedObject(), step: 4 * sizeof(short), rows: 2, columns: 4);
            }
            finally
            {
                handle.Free();
            }

            CollectionAssert.AreEqual(new[] { 4L, 2L }, ZarrTestHelper.GetShape(ArrayPath));
            CollectionAssert.AreEqual(new short[] { 1, 5, 2, 6, 3, 7, 4, 8 }, ZarrTestHelper.ReadElements<short>(Path.Combine(ArrayPath, "c", "0", "0")));
        }

        [TestMethod]
        public void ConcurrentWriters_ShareStoreAndGroups()
        {
            // Every writer ensures the same ancestor groups exist, so their metadata writes overlap.
            const int WriterCount = 16;
            Parallel.For(0, WriterCount, i =>
            {
                using var writer = ZarrTestHelper.CreateArrayWriter<int>(storePath, $"group/data{i}", Array.Empty<int>(), ZarrTestHelper.CreateSettings(chunkLength: 2));
                ZarrTestHelper.WriteRows(writer, 0, 1, 2, 3, 4);
            });

            for (int i = 0; i < WriterCount; i++)
            {
                CollectionAssert.AreEqual(new[] { 5L }, ZarrTestHelper.GetShape(Path.Combine(storePath, "group", $"data{i}")));
            }

            Assert.AreEqual("group", (string?)ZarrTestHelper.ReadMetadata(Path.Combine(storePath, "group"))["node_type"]);
            var temporaryFiles = Directory.GetFiles(storePath, "*.tmp", SearchOption.AllDirectories);
            Assert.IsEmpty(temporaryFiles, string.Join(", ", temporaryFiles.Select(Path.GetFileName)));
        }

        [TestMethod]
        public void EmptyArray_HasNoChunks()
        {
            ZarrTestHelper.CreateArrayWriter<int>(storePath, ArrayName, Array.Empty<int>(), ZarrTestHelper.CreateSettings(chunkLength: 2)).Dispose();
            CollectionAssert.AreEqual(new[] { 0L }, ZarrTestHelper.GetShape(ArrayPath));
            Assert.IsFalse(Directory.Exists(Path.Combine(ArrayPath, "c")));
        }

        [TestMethod]
        public void Write_AfterDisposal_RaisesObjectDisposedException()
        {
            var writer = ZarrTestHelper.CreateArrayWriter<int>(storePath, ArrayName, Array.Empty<int>(), ZarrTestHelper.CreateSettings(chunkLength: 2));
            writer.Dispose();
            Assert.ThrowsExactly<ObjectDisposedException>(() => ZarrTestHelper.WriteRows(writer, 1));
        }

        [TestMethod]
        public void PartialRow_RaisesArgumentException()
        {
            using var writer = ZarrTestHelper.CreateArrayWriter<int>(storePath, ArrayName, new[] { 2 }, ZarrTestHelper.CreateSettings(chunkLength: 2));
            Assert.ThrowsExactly<ArgumentException>(() => ZarrTestHelper.WriteRows(writer, 1, 2, 3));
        }
    }
}
