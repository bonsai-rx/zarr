using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCV.Net;

namespace Bonsai.Zarr.Tests
{
    [TestClass]
    public class TestInteropStores
    {
        // Writes the store read by tests/interop/validate.py, which checks it against zarr-python
        // and xarray. The values follow the formulas in that script, so both must change together.
        const int ValueCount = 10;
        const int RowCount = 7;
        const int RowLength = 3;
        const int ChunkLength = 4;
        const int FrameCount = 6;
        const int BufferCount = 3;
        const int BufferLength = 5;
        const int ChannelCount = 4;
        const int InterruptedCount = 10;
        const int InterruptedContiguousCount = 10000;
        const int InterruptedContiguousFrameCount = 30000;

        public struct Sample
        {
            public double Time;
            public short[] Signal;
        }

        public class Buffer
        {
            public ulong[] Clock { get; set; } = Array.Empty<ulong>();

            public double[] Data { get; set; } = Array.Empty<double>();
        }

        public class MatBuffer
        {
            public ulong[] Clock { get; set; } = Array.Empty<ulong>();

            public Mat? Amplifier { get; set; }
        }

        static string StorePath => Path.Combine(AppContext.BaseDirectory, "interop.zarr");

        static ZarrWriter CreateWriter(string path, string? bufferSelector = null, int? chunkLength = ChunkLength)
        {
            return new ZarrWriter
            {
                Path = StorePath,
                Name = path,
                ChunkLength = chunkLength,
                BufferSelector = bufferSelector
            };
        }

        static async Task WriteAsync<T>(string dataType, Func<int, T> getValue) where T : unmanaged
        {
            var values = Enumerable.Range(0, ValueCount).Select(getValue).ToArray();
            await ZarrTestHelper.Write(CreateWriter($"values/{dataType}"), values.ToObservable());
            await ZarrTestHelper.Write(CreateWriter($"contiguous_values/{dataType}", chunkLength: null), values.ToObservable());

            var rows = Enumerable.Range(0, RowCount)
                .Select(row => Enumerable.Range(row * RowLength, RowLength).Select(getValue).ToArray())
                .ToArray();
            await ZarrTestHelper.Write(CreateWriter($"rows/{dataType}"), rows.ToObservable());
            await ZarrTestHelper.Write(CreateWriter($"contiguous_rows/{dataType}", chunkLength: null), rows.ToObservable());
        }

        static Sample CreateSample(int i)
        {
            return new Sample { Time = i * 0.5, Signal = new[] { (short)(2 * i), (short)(2 * i + 1) } };
        }

        static ulong[] CreateClock(int buffer)
        {
            return Enumerable.Range(buffer * BufferLength, BufferLength).Select(t => (ulong)t).ToArray();
        }

        static Mat CreateAmplifier(int buffer)
        {
            var data = new short[ChannelCount, BufferLength];
            for (int channel = 0; channel < ChannelCount; channel++)
            {
                for (int sample = 0; sample < BufferLength; sample++)
                {
                    data[channel, sample] = (short)(channel * 100 + buffer * BufferLength + sample);
                }
            }

            return Mat.FromArray(data);
        }

        static void CopySharedFile(string sourcePath, string destinationPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var destination = File.Create(destinationPath);
            source.CopyTo(destination);
        }

        static void CopyOpenNode(string sourcePath, string destinationPath)
        {
            foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
            {
                CopySharedFile(file, Path.Combine(destinationPath, file.Substring(sourcePath.Length + 1)));
            }
        }

        static void WriteInterrupted<T>(string path, string copyPath, Func<int, T> getValue, int count = InterruptedCount, int? chunkLength = ChunkLength)
        {
            // The copy is taken while the writer is still open, as a crash would leave the store.
            var source = new Subject<T>();
            var subscription = ZarrTestHelper.Write(CreateWriter(path, chunkLength: chunkLength), source).Subscribe();
            for (int i = 0; i < count; i++)
            {
                source.OnNext(getValue(i));
            }

            var sourcePath = Path.Combine(StorePath, path);
            var destinationPath = Path.Combine(StorePath, copyPath);
            CopyOpenNode(sourcePath, destinationPath);
            CopySharedFile(
                Path.Combine(Path.GetDirectoryName(sourcePath)!, "zarr.json"),
                Path.Combine(Path.GetDirectoryName(destinationPath)!, "zarr.json"));
            subscription.Dispose();
        }

        [TestMethod]
        public async Task InteropStore_WritesEveryDataTypeAndFrame()
        {
            ZarrTestHelper.DeleteStore(StorePath);
            await WriteAsync("bool", i => i % 3 == 0);
            await WriteAsync("int8", i => (sbyte)((i - 10) * 5));
            await WriteAsync("int16", i => (short)((i - 10) * 1000));
            await WriteAsync("int32", i => (i - 10) * 100000);
            await WriteAsync("int64", i => (i - 10) * 10000000000L);
            await WriteAsync("uint8", i => (byte)(i * 10));
            await WriteAsync("uint16", i => (ushort)(i * 2000));
            await WriteAsync("uint32", i => (uint)i * 200000000u);
            await WriteAsync("uint64", i => (ulong)i * 800000000000000000ul);
            await WriteAsync("float32", i => (i - 10) * 0.25f);
            await WriteAsync("float64", i => (i - 10) * 0.1);

            var source = new Subject<double>();
            var subscription = ZarrTestHelper.Write(CreateWriter("disposed/float64"), source).Subscribe();
            for (int i = 0; i < 6; i++)
            {
                source.OnNext(i);
            }

            subscription.Dispose();

            await ZarrTestHelper.Write(CreateWriter("frame"), Enumerable.Range(0, FrameCount).Select(CreateSample).ToObservable());
            await ZarrTestHelper.Write(CreateWriter("contiguous_frame", chunkLength: null), Enumerable.Range(0, FrameCount).Select(CreateSample).ToObservable());

            var buffers = Enumerable.Range(0, BufferCount).Select(i => new Buffer
            {
                Clock = CreateClock(i),
                Data = CreateClock(i).Select(t => t * 0.25).ToArray()
            });
            await ZarrTestHelper.Write(CreateWriter("buffer", bufferSelector: "Clock"), buffers.ToObservable());

            if (ZarrTestHelper.IsOpenCVAvailable)
            {
                var matBuffers = Enumerable.Range(0, BufferCount).Select(i => new MatBuffer
                {
                    Clock = CreateClock(i),
                    Amplifier = CreateAmplifier(i)
                });
                await ZarrTestHelper.Write(CreateWriter("mat", bufferSelector: "Clock"), matBuffers.ToObservable());
            }

            WriteInterrupted("closed/float64", "interrupted/float64", i => (double)i);
            WriteInterrupted("closed/frame", "interrupted/frame", CreateSample);
            WriteInterrupted("closed/contiguous", "interrupted/contiguous", i => (double)i, InterruptedContiguousCount, chunkLength: null);
            WriteInterrupted("closed/contiguous_frame", "interrupted/contiguous_frame", CreateSample, InterruptedContiguousFrameCount, chunkLength: null);
            CollectionAssert.AreEqual(new[] { 0L }, ZarrTestHelper.GetShape(Path.Combine(StorePath, "interrupted", "float64")));
        }
    }
}
