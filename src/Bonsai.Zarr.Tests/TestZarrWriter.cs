using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Bonsai.Expressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace Bonsai.Zarr.Tests
{
    [TestClass]
    public class TestZarrWriter
    {
        const string ArrayName = "data";
        string storePath = string.Empty;

        public enum Level : short
        {
            Low = -1,
            High = 1
        }

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

        ZarrWriter CreateWriter(int chunkLength)
        {
            return new ZarrWriter
            {
                Path = storePath,
                Name = ArrayName,
                ChunkLength = chunkLength
            };
        }

        string GetDataType()
        {
            return (string)ZarrTestHelper.ReadMetadata(ArrayPath)["data_type"]!;
        }

        [TestMethod]
        public async Task Write_ReturnsSourceSequenceUnchanged()
        {
            var values = new[] { 1.5, -2.0, 3.25 };
            var result = await ZarrTestHelper.Write(CreateWriter(chunkLength: 2), values.ToObservable()).ToArray();
            CollectionAssert.AreEqual(values, result);
        }

        [TestMethod]
        public void DefaultChunkLength_Is65536()
        {
            Assert.AreEqual(65536, new ZarrWriter().ChunkLength);
        }

        [TestMethod]
        public async Task Values_WriteOneDimensionalArray()
        {
            await ZarrTestHelper.Write(CreateWriter(chunkLength: 2), Observable.Range(1, 3));
            Assert.AreEqual("int32", GetDataType());
            CollectionAssert.AreEqual(new[] { 3L }, ZarrTestHelper.GetShape(ArrayPath));
            CollectionAssert.AreEqual(new[] { 1, 2 }, ZarrTestHelper.ReadElements<int>(Path.Combine(ArrayPath, "c", "0")));
            CollectionAssert.AreEqual(new[] { 3, 0 }, ZarrTestHelper.ReadElements<int>(Path.Combine(ArrayPath, "c", "1")));
        }

        [TestMethod]
        public async Task ByteValues_WriteUInt8Array()
        {
            await ZarrTestHelper.Write(CreateWriter(chunkLength: 2), new byte[] { 1, 2, 3 }.ToObservable());
            Assert.AreEqual("uint8", GetDataType());
        }

        [TestMethod]
        public async Task EnumValues_WriteUnderlyingType()
        {
            await ZarrTestHelper.Write(CreateWriter(chunkLength: 2), new[] { Level.Low, Level.High }.ToObservable());
            Assert.AreEqual("int16", GetDataType());
            CollectionAssert.AreEqual(new short[] { -1, 1 }, ZarrTestHelper.ReadElements<short>(Path.Combine(ArrayPath, "c", "0")));
        }

        [TestMethod]
        public async Task Arrays_WriteTwoDimensionalArray()
        {
            var rows = new[] { new long[] { 1, 2, 3 }, new long[] { 4, 5, 6 }, new long[] { 7, 8, 9 } };
            await ZarrTestHelper.Write(CreateWriter(chunkLength: 2), rows.ToObservable());

            var metadata = ZarrTestHelper.ReadMetadata(ArrayPath);
            Assert.AreEqual("int64", (string?)metadata["data_type"]);
            CollectionAssert.AreEqual(new[] { 3L, 3L }, ZarrTestHelper.GetShape(ArrayPath));
            CollectionAssert.AreEqual(new[] { 2, 3 }, metadata["chunk_grid"]!["configuration"]!["chunk_shape"]!.Values<int>().ToArray());
            CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4, 5, 6 }, ZarrTestHelper.ReadElements<long>(Path.Combine(ArrayPath, "c", "0", "0")));
            CollectionAssert.AreEqual(new long[] { 7, 8, 9, 0, 0, 0 }, ZarrTestHelper.ReadElements<long>(Path.Combine(ArrayPath, "c", "1", "0")));
        }

        [TestMethod]
        public async Task ArrayLengthMismatch_RaisesInvalidOperationException()
        {
            var rows = new[] { new byte[] { 1, 2 }, new byte[] { 3, 4 }, new byte[] { 5 } };
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await ZarrTestHelper.Write(CreateWriter(chunkLength: 4), rows.ToObservable()));

            CollectionAssert.AreEqual(new[] { 2L, 2L }, ZarrTestHelper.GetShape(ArrayPath));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 0, 0, 0, 0 }, File.ReadAllBytes(Path.Combine(ArrayPath, "c", "0", "0")));
        }

        [TestMethod]
        public void Disposal_WithoutCompletion_PersistsData()
        {
            // Stopping a workflow disposes the subscription without completing the sequence.
            var source = new Subject<float>();
            var subscription = ZarrTestHelper.Write(CreateWriter(chunkLength: 4), source).Subscribe();
            source.OnNext(1);
            source.OnNext(2);
            source.OnNext(3);
            CollectionAssert.AreEqual(new[] { 0L }, ZarrTestHelper.GetShape(ArrayPath));
            subscription.Dispose();

            CollectionAssert.AreEqual(new[] { 3L }, ZarrTestHelper.GetShape(ArrayPath));
            CollectionAssert.AreEqual(new[] { 1f, 2f, 3f, 0f }, ZarrTestHelper.ReadElements<float>(Path.Combine(ArrayPath, "c", "0")));
        }

        [TestMethod]
        public async Task EmptySequence_CreatesNoArray()
        {
            await ZarrTestHelper.Write(CreateWriter(chunkLength: 2), Observable.Empty<int>()).DefaultIfEmpty();
            Assert.IsFalse(Directory.Exists(storePath));
        }

        [TestMethod]
        public async Task ExistingArray_RaisesIOException()
        {
            await ZarrTestHelper.Write(CreateWriter(chunkLength: 2), Observable.Return(1));
            await Assert.ThrowsExactlyAsync<IOException>(
                async () => await ZarrTestHelper.Write(CreateWriter(chunkLength: 2), Observable.Return(2)));
            CollectionAssert.AreEqual(new[] { 1, 0 }, ZarrTestHelper.ReadElements<int>(Path.Combine(ArrayPath, "c", "0")));
        }

        [TestMethod]
        public async Task ExistingArray_WithOverwrite_IsReplaced()
        {
            await ZarrTestHelper.Write(CreateWriter(chunkLength: 2), Observable.Range(1, 5));
            var writer = CreateWriter(chunkLength: 2);
            writer.Overwrite = true;
            await ZarrTestHelper.Write(writer, Observable.Return(42));

            CollectionAssert.AreEqual(new[] { 1L }, ZarrTestHelper.GetShape(ArrayPath));
            CollectionAssert.AreEqual(new[] { 42, 0 }, ZarrTestHelper.ReadElements<int>(Path.Combine(ArrayPath, "c", "0")));
            Assert.IsFalse(File.Exists(Path.Combine(ArrayPath, "c", "1")));
        }

        [TestMethod]
        public async Task ExistingGroup_WithOverwrite_IsReplacedByArray()
        {
            var writer = CreateWriter(chunkLength: 2);
            writer.Name = "group/data";
            await ZarrTestHelper.Write(writer, Observable.Return(1));

            writer = CreateWriter(chunkLength: 2);
            writer.Name = "group";
            writer.Overwrite = true;
            await ZarrTestHelper.Write(writer, Observable.Return(2));

            var groupPath = Path.Combine(storePath, "group");
            Assert.AreEqual("array", (string?)ZarrTestHelper.ReadMetadata(groupPath)["node_type"]);
            CollectionAssert.AreEqual(new[] { 2, 0 }, ZarrTestHelper.ReadElements<int>(Path.Combine(groupPath, "c", "0")));
            Assert.IsFalse(Directory.Exists(Path.Combine(groupPath, "data")));
        }

        [TestMethod]
        public async Task NestedPath_CreatesIntermediateGroups()
        {
            var writer = CreateWriter(chunkLength: 2);
            writer.Name = "device/register/data";
            await ZarrTestHelper.Write(writer, Observable.Return(1.0));

            var expectedGroup = JObject.Parse("{\"attributes\":{},\"zarr_format\":3,\"node_type\":\"group\"}");
            foreach (var groupPath in new[] { storePath, Path.Combine(storePath, "device"), Path.Combine(storePath, "device", "register") })
            {
                Assert.IsTrue(JToken.DeepEquals(expectedGroup, ZarrTestHelper.ReadMetadata(groupPath)), groupPath);
            }

            var arrayPath = Path.Combine(storePath, "device", "register", "data");
            Assert.AreEqual("array", (string?)ZarrTestHelper.ReadMetadata(arrayPath)["node_type"]);
        }

        [TestMethod]
        public async Task DimensionNames_DerivedFromArrayName()
        {
            await ZarrTestHelper.Write(CreateWriter(chunkLength: 2), Observable.Return(new short[] { 1, 2 }));
            CollectionAssert.AreEqual(new[] { "data_dim_0", "data_dim_1" }, ZarrTestHelper.GetDimensionNames(ArrayPath));
        }

        [TestMethod]
        public async Task ReservedNodeName_RaisesArgumentException()
        {
            var writer = CreateWriter(chunkLength: 2);
            writer.Name = "__data";
            await Assert.ThrowsExactlyAsync<ArgumentException>(
                async () => await ZarrTestHelper.Write(writer, Observable.Return(1)));
        }

        [TestMethod]
        [DataRow(@"C:\data\session.zarr")]
        [DataRow(@"device\temperature")]
        [DataRow("C:/data/session.zarr")]
        public async Task FileSystemPathAsName_RaisesArgumentException(string name)
        {
            // A store folder entered as the name is rejected rather than nested inside the store.
            var writer = CreateWriter(chunkLength: 2);
            writer.Name = name;
            await Assert.ThrowsExactlyAsync<ArgumentException>(
                async () => await ZarrTestHelper.Write(writer, Observable.Return(1)));
            Assert.IsFalse(Directory.Exists(storePath));
        }

        [TestMethod]
        public async Task LeadingSlash_WritesRelativeToStoreRoot()
        {
            // Names returned by zarr-python start with a slash, which refers to the store root.
            var writer = CreateWriter(chunkLength: 2);
            writer.Name = "/device/temperature";
            await ZarrTestHelper.Write(writer, Observable.Return(1));
            CollectionAssert.AreEqual(new[] { 1L }, ZarrTestHelper.GetShape(Path.Combine(storePath, "device", "temperature")));
        }

        [TestMethod]
        public void DisplayName_IsNodeName()
        {
            var writer = CreateWriter(chunkLength: 2);
            writer.Name = "device/temperature";
            Assert.AreEqual("device/temperature", ExpressionBuilder.GetElementDisplayName(writer));
            writer.Name = null;
            Assert.AreEqual(nameof(ZarrWriter), ExpressionBuilder.GetElementDisplayName(writer));
        }

        [TestMethod]
        public void UnsupportedElementType_RaisesOnBuild()
        {
            var error = Assert.ThrowsExactly<WorkflowBuildException>(
                () => ZarrTestHelper.Write(CreateWriter(chunkLength: 2), Observable.Return(1m)));
            Assert.IsInstanceOfType<NotSupportedException>(error.InnerException);
        }
    }
}
