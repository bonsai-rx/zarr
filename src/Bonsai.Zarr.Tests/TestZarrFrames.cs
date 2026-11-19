using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Bonsai.Expressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCV.Net;

namespace Bonsai.Zarr.Tests
{
    [TestClass]
    public class TestZarrFrames
    {
        const string GroupName = "frame";
        string storePath = string.Empty;

        public struct Point
        {
            public float X;
            public float Y;
        }

        public struct Sample
        {
            public double Time;
            public short[] Signal;
        }

        public struct Tracked
        {
            public int Id;
            public Point Position;
        }

        public class Buffer
        {
            public ulong[] Clock { get; set; } = Array.Empty<ulong>();

            public double[] Data { get; set; } = Array.Empty<double>();

            public int Count { get; set; }
        }

        public class MatBuffer
        {
            public ulong[] Clock { get; set; } = Array.Empty<ulong>();

            public Mat? Amplifier { get; set; }
        }

        public struct Labeled
        {
            public double Value;
            public string Label;
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

        string GetArrayPath(string name) => Path.Combine(storePath, GroupName, name);

        ZarrWriter CreateWriter(int? chunkLength, string? selector = null, string? bufferSelector = null)
        {
            return new ZarrWriter
            {
                Path = storePath,
                Name = GroupName,
                ChunkLength = chunkLength,
                ValueSelector = selector,
                BufferSelector = bufferSelector
            };
        }

        static Sample CreateSample(int i)
        {
            return new Sample { Time = i * 0.5, Signal = new[] { (short)(2 * i), (short)(2 * i + 1) } };
        }

        static Buffer CreateBuffer(int i, int count = 3)
        {
            return new Buffer
            {
                Clock = Enumerable.Range(i * count, count).Select(t => (ulong)t).ToArray(),
                Data = Enumerable.Range(i * count, count).Select(t => t * 0.25).ToArray(),
                Count = count
            };
        }

        [TestMethod]
        public async Task Members_WriteGroupOfArrays()
        {
            await ZarrTestHelper.Write(CreateWriter(chunkLength: 2), Observable.Range(0, 3).Select(CreateSample));

            Assert.AreEqual("group", (string?)ZarrTestHelper.ReadMetadata(Path.Combine(storePath, GroupName))["node_type"]);
            CollectionAssert.AreEqual(new[] { 3L }, ZarrTestHelper.GetShape(GetArrayPath("Time")));
            CollectionAssert.AreEqual(new[] { 3L, 2L }, ZarrTestHelper.GetShape(GetArrayPath("Signal")));
            CollectionAssert.AreEqual(new[] { 0.0, 0.5 }, ZarrTestHelper.ReadElements<double>(Path.Combine(GetArrayPath("Time"), "c", "0")));
            CollectionAssert.AreEqual(new short[] { 4, 5, 0, 0 }, ZarrTestHelper.ReadElements<short>(Path.Combine(GetArrayPath("Signal"), "c", "1", "0")));
        }

        [TestMethod]
        public async Task NoChunkLength_WritesEachArrayToSingleFile()
        {
            await ZarrTestHelper.Write(CreateWriter(chunkLength: null), Observable.Range(0, 3).Select(CreateSample));

            CollectionAssert.AreEqual(new[] { 3L }, ZarrTestHelper.GetChunkShape(GetArrayPath("Time")));
            CollectionAssert.AreEqual(new[] { 3L, 2L }, ZarrTestHelper.GetChunkShape(GetArrayPath("Signal")));
            CollectionAssert.AreEqual(new[] { 0.0, 0.5, 1.0 }, ZarrTestHelper.ReadElements<double>(Path.Combine(GetArrayPath("Time"), "c", "0")));
            CollectionAssert.AreEqual(new short[] { 0, 1, 2, 3, 4, 5 }, ZarrTestHelper.ReadElements<short>(Path.Combine(GetArrayPath("Signal"), "c", "0", "0")));
        }

        [TestMethod]
        public async Task GroupArrays_ShareFirstDimension()
        {
            await ZarrTestHelper.Write(CreateWriter(chunkLength: 2), Observable.Return(CreateSample(0)));
            CollectionAssert.AreEqual(new[] { "frame_dim_0" }, ZarrTestHelper.GetDimensionNames(GetArrayPath("Time")));
            CollectionAssert.AreEqual(new[] { "frame_dim_0", "Signal_dim_1" }, ZarrTestHelper.GetDimensionNames(GetArrayPath("Signal")));
        }

        [TestMethod]
        public async Task NestedMembers_FlattenToDottedNames()
        {
            var values = new[] { new Tracked { Id = 1, Position = new Point { X = 1.5f, Y = -2 } } };
            await ZarrTestHelper.Write(CreateWriter(chunkLength: 2), values.ToObservable());

            var arrays = Directory.GetDirectories(Path.Combine(storePath, GroupName)).Select(Path.GetFileName).OrderBy(name => name).ToArray();
            CollectionAssert.AreEqual(new[] { "Id", "Position.X", "Position.Y" }, arrays);
            CollectionAssert.AreEqual(new[] { -2f, 0f }, ZarrTestHelper.ReadElements<float>(Path.Combine(GetArrayPath("Position.Y"), "c", "0")));
        }

        [TestMethod]
        public async Task Selector_WritesOnlySelectedMembers()
        {
            var values = new[] { new Tracked { Id = 1, Position = new Point { X = 1.5f, Y = -2 } } };
            await ZarrTestHelper.Write(CreateWriter(chunkLength: 2, selector: "Position.X,Id"), values.ToObservable());

            var arrays = Directory.GetDirectories(Path.Combine(storePath, GroupName)).Select(Path.GetFileName).OrderBy(name => name).ToArray();
            CollectionAssert.AreEqual(new[] { "Id", "Position.X" }, arrays);
        }

        [TestMethod]
        public async Task BufferSelector_WritesOneRowPerSample()
        {
            await ZarrTestHelper.Write(
                CreateWriter(chunkLength: 4, selector: "Clock,Data", bufferSelector: "Clock"),
                Observable.Range(0, 2).Select(i => CreateBuffer(i)));

            CollectionAssert.AreEqual(new[] { 6L }, ZarrTestHelper.GetShape(GetArrayPath("Clock")));
            CollectionAssert.AreEqual(new[] { 6L }, ZarrTestHelper.GetShape(GetArrayPath("Data")));
            CollectionAssert.AreEqual(new ulong[] { 4, 5, 0, 0 }, ZarrTestHelper.ReadElements<ulong>(Path.Combine(GetArrayPath("Clock"), "c", "1")));
            CollectionAssert.AreEqual(new[] { 0.0, 0.25, 0.5, 0.75 }, ZarrTestHelper.ReadElements<double>(Path.Combine(GetArrayPath("Data"), "c", "0")));
        }

        [TestMethod]
        public async Task BufferSelector_OnElementItself_WritesSamples()
        {
            var writer = CreateWriter(chunkLength: 4, bufferSelector: "it");
            await ZarrTestHelper.Write(writer, Observable.Range(0, 2).Select(i => new[] { i, i + 10 }));

            var arrayPath = Path.Combine(storePath, GroupName);
            CollectionAssert.AreEqual(new[] { 4L }, ZarrTestHelper.GetShape(arrayPath));
            CollectionAssert.AreEqual(new[] { 0, 10, 1, 11 }, ZarrTestHelper.ReadElements<int>(Path.Combine(arrayPath, "c", "0")));
        }

        [TestMethod]
        public async Task BufferLengthMismatch_RaisesWithoutWritingElement()
        {
            // The arrays always keep the same length when an element is rejected.
            var mismatched = CreateBuffer(1);
            mismatched.Data = new[] { 1.0, 2.0 };
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await ZarrTestHelper.Write(
                CreateWriter(chunkLength: 4, selector: "Clock,Data", bufferSelector: "Clock"),
                new[] { CreateBuffer(0), mismatched }.ToObservable()));

            CollectionAssert.AreEqual(new[] { 3L }, ZarrTestHelper.GetShape(GetArrayPath("Clock")));
            CollectionAssert.AreEqual(new[] { 3L }, ZarrTestHelper.GetShape(GetArrayPath("Data")));
        }

        [TestMethod]
        public void SingleValueWithBuffer_RaisesOnBuild()
        {
            // A single value per element grows at a different rate from the buffered samples.
            var error = Assert.ThrowsExactly<WorkflowBuildException>(() => ZarrTestHelper.Write(
                CreateWriter(chunkLength: 4, bufferSelector: "Clock"),
                Observable.Return(CreateBuffer(0))));
            Assert.IsInstanceOfType<InvalidOperationException>(error.InnerException);
        }

        [TestMethod]
        public void MatrixWithoutBuffer_RaisesOnBuild()
        {
            var error = Assert.ThrowsExactly<WorkflowBuildException>(() => ZarrTestHelper.Write(
                CreateWriter(chunkLength: 4, selector: "Amplifier"),
                Observable.Return(new MatBuffer())));
            Assert.IsInstanceOfType<NotSupportedException>(error.InnerException);
        }

        [TestMethod]
        public void StringMember_RaisesOnBuild()
        {
            var error = Assert.ThrowsExactly<WorkflowBuildException>(() => ZarrTestHelper.Write(
                CreateWriter(chunkLength: 4),
                Observable.Return(new Labeled { Value = 1, Label = "a" })));
            Assert.IsInstanceOfType<NotSupportedException>(error.InnerException);
        }

        [TestMethod]
        public void DuplicateSelection_RaisesOnBuild()
        {
            var error = Assert.ThrowsExactly<WorkflowBuildException>(() => ZarrTestHelper.Write(
                CreateWriter(chunkLength: 4, selector: "Time,Time"),
                Observable.Return(CreateSample(0))));
            Assert.IsInstanceOfType<InvalidOperationException>(error.InnerException);
        }

        [TestMethod]
        public async Task ExistingGroup_WithOverwrite_IsReplaced()
        {
            await ZarrTestHelper.Write(CreateWriter(chunkLength: 2), Observable.Range(0, 3).Select(CreateSample));
            var writer = CreateWriter(chunkLength: 2);
            writer.Overwrite = true;
            await ZarrTestHelper.Write(writer, Observable.Return(CreateSample(5)));

            CollectionAssert.AreEqual(new[] { 1L }, ZarrTestHelper.GetShape(GetArrayPath("Time")));
            Assert.IsFalse(Directory.Exists(Path.Combine(GetArrayPath("Time"), "c", "1")));
        }

        [TestMethod]
        public async Task ExistingGroup_RaisesIOException()
        {
            // The error from an existing group is raised while the sequence has not terminated.
            await ZarrTestHelper.Write(CreateWriter(chunkLength: 2), Observable.Return(CreateSample(0)));
            await Assert.ThrowsExactlyAsync<IOException>(
                async () => await ZarrTestHelper.Write(CreateWriter(chunkLength: 2), Observable.Return(CreateSample(1)).Concat(Observable.Never<Sample>())));
            CollectionAssert.AreEqual(new[] { 0.0, 0.0 }, ZarrTestHelper.ReadElements<double>(Path.Combine(GetArrayPath("Time"), "c", "0")));
        }

        [TestMethod]
        public async Task ExistingGroup_WithChangedMembers_IsReplaced()
        {
            // Overwriting replaces the whole group, including arrays from members that are no longer
            // written.
            await ZarrTestHelper.Write(CreateWriter(chunkLength: 2), Observable.Return(CreateSample(0)));
            var writer = CreateWriter(chunkLength: 2);
            writer.Overwrite = true;
            var values = new[] { new Tracked { Id = 1, Position = new Point { X = 1.5f, Y = -2 } } };
            await ZarrTestHelper.Write(writer, values.ToObservable());

            var arrays = Directory.GetDirectories(Path.Combine(storePath, GroupName)).Select(Path.GetFileName).OrderBy(name => name).ToArray();
            CollectionAssert.AreEqual(new[] { "Id", "Position.X", "Position.Y" }, arrays);
        }

        [TestMethod]
        public async Task MatrixBuffer_WritesOneRowPerSample()
        {
            // A matrix buffer has one row per channel and one column per sample, as in Bonsai.Dsp.
            ZarrTestHelper.RequireOpenCV();
            var amplifier = Mat.FromArray(new short[,] { { 1, 2, 3 }, { 4, 5, 6 } });
            var frame = new MatBuffer { Clock = new ulong[] { 10, 11, 12 }, Amplifier = amplifier };
            await ZarrTestHelper.Write(CreateWriter(chunkLength: 4, bufferSelector: "Clock"), Observable.Return(frame));

            var amplifierPath = GetArrayPath("Amplifier");
            Assert.AreEqual("int16", (string?)ZarrTestHelper.ReadMetadata(amplifierPath)["data_type"]);
            CollectionAssert.AreEqual(new[] { 3L, 2L }, ZarrTestHelper.GetShape(amplifierPath));
            CollectionAssert.AreEqual(new short[] { 1, 4, 2, 5, 3, 6, 0, 0 }, ZarrTestHelper.ReadElements<short>(Path.Combine(amplifierPath, "c", "0", "0")));
            CollectionAssert.AreEqual(new[] { 3L }, ZarrTestHelper.GetShape(GetArrayPath("Clock")));
        }
    }
}
