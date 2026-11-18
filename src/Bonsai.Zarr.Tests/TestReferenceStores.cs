using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bonsai.Zarr.Tests
{
    [TestClass]
    public class TestReferenceStores
    {
        // The fixtures are written by zarr-python with tests/interop/generate_fixtures.py.
        const string ArrayName = "data";
        string storePath = string.Empty;

        public struct Sample
        {
            public double Time;
            public short[] Signal;
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

        static string GetFixturePath(string name)
        {
            return Path.Combine(ZarrTestHelper.FixturesPath, name);
        }

        ZarrWriter CreateWriter(int chunkLength)
        {
            return new ZarrWriter
            {
                Path = storePath,
                Name = ArrayName,
                ChunkLength = chunkLength
            };
        }

        [TestMethod]
        public async Task UInt16Array_MatchesReference()
        {
            // Covers the padding of the edge chunk with the fill value.
            await ZarrTestHelper.Write(CreateWriter(chunkLength: 4), Observable.Range(0, 10).Select(i => (ushort)i));
            ZarrTestHelper.AssertNodeEqual(GetFixturePath("a_u16_raw"), Path.Combine(storePath, ArrayName));
        }

        [TestMethod]
        public async Task BoolArray_MatchesReference()
        {
            // Covers the bytes codec with no endian configuration for one-byte types.
            await ZarrTestHelper.Write(CreateWriter(chunkLength: 3), new[] { true, false, true }.ToObservable());
            ZarrTestHelper.AssertNodeEqual(GetFixturePath("j_bool"), Path.Combine(storePath, ArrayName));
        }

        [TestMethod]
        public async Task Float64Matrix_MatchesReference()
        {
            // Covers two-dimensional chunk keys and the padding of a partial edge chunk.
            var rows = Enumerable.Range(0, 5).Select(row => Enumerable.Range(row * 3, 3).Select(i => i / 2.0).ToArray());
            await ZarrTestHelper.Write(CreateWriter(chunkLength: 2), rows.ToObservable());
            ZarrTestHelper.AssertNodeEqual(GetFixturePath("c_f64_2d"), Path.Combine(storePath, ArrayName));
        }

        [TestMethod]
        public async Task FrameGroup_MatchesReference()
        {
            // Covers the store root group, a frame group and the shared first dimension.
            var writer = CreateWriter(chunkLength: 4);
            writer.Name = "frame";
            var samples = Enumerable.Range(0, 6).Select(i => new Sample
            {
                Time = i * 0.5,
                Signal = new[] { (short)(2 * i), (short)(2 * i + 1) }
            });

            await ZarrTestHelper.Write(writer, samples.ToObservable());
            ZarrTestHelper.AssertNodeEqual(GetFixturePath("d_group"), storePath);
        }
    }
}
