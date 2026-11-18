using System;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace Bonsai.Zarr.Tests
{
    static class ZarrTestHelper
    {
        public static string FixturesPath => Path.Combine(AppContext.BaseDirectory, "Fixtures");

        public static string CreateStorePath()
        {
            return Path.Combine(Path.GetTempPath(), "Bonsai.Zarr.Tests", Guid.NewGuid().ToString("N"));
        }

        public static void DeleteStore(string storePath)
        {
            if (Directory.Exists(storePath))
            {
                Directory.Delete(storePath, recursive: true);
            }
        }

        public static bool IsOpenCVAvailable => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public static void RequireOpenCV()
        {
            if (!IsOpenCVAvailable)
            {
                Assert.Inconclusive("The native OpenCV binaries are only available on Windows.");
            }
        }

        public static IObservable<TSource> Write<TSource>(ZarrWriter writer, IObservable<TSource> source)
        {
            return WorkflowTestHelper.BuildObservable<TSource>(Expression.Constant(source, typeof(IObservable<TSource>)), writer);
        }

        public static ZarrArraySettings CreateSettings(int? chunkLength)
        {
            return new ZarrArraySettings { ChunkLength = chunkLength };
        }

        public static ZarrArrayWriter CreateArrayWriter<T>(string storePath, string arrayPath, int[] rowShape, ZarrArraySettings settings)
            where T : unmanaged
        {
            return new ZarrArrayWriter(storePath, arrayPath, ZarrDataType.FromType(typeof(T)), rowShape, settings);
        }

        public static void WriteRows<T>(ZarrArrayWriter writer, params T[] values) where T : unmanaged
        {
            writer.Write(MemoryMarshal.AsBytes(values.AsSpan()));
        }

        public static JObject ReadMetadata(string nodePath)
        {
            return JObject.Parse(File.ReadAllText(Path.Combine(nodePath, "zarr.json")));
        }

        public static long[] GetShape(string arrayPath)
        {
            return ReadMetadata(arrayPath)["shape"]!.Values<long>().ToArray();
        }

        public static long[] GetChunkShape(string arrayPath)
        {
            return ReadMetadata(arrayPath)["chunk_grid"]!["configuration"]!["chunk_shape"]!.Values<long>().ToArray();
        }

        public static string[] GetDimensionNames(string arrayPath)
        {
            return ReadMetadata(arrayPath)["dimension_names"]!.Values<string>().Select(name => name!).ToArray();
        }

        public static T[] ReadElements<T>(string chunkPath) where T : unmanaged
        {
            return MemoryMarshal.Cast<byte, T>(File.ReadAllBytes(chunkPath).AsSpan()).ToArray();
        }

        public static void AssertNodeEqual(string expectedPath, string actualPath)
        {
            var expectedFiles = Directory.GetFiles(expectedPath, "*", SearchOption.AllDirectories)
                .Select(path => GetRelativePath(expectedPath, path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var actualFiles = Directory.GetFiles(actualPath, "*", SearchOption.AllDirectories)
                .Select(path => GetRelativePath(actualPath, path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            CollectionAssert.AreEqual(expectedFiles, actualFiles, "The stores contain different keys.");

            foreach (var key in expectedFiles)
            {
                var expectedFile = Path.Combine(expectedPath, key);
                var actualFile = Path.Combine(actualPath, key);
                if (Path.GetFileName(key) == "zarr.json")
                {
                    var expected = JObject.Parse(File.ReadAllText(expectedFile));
                    var actual = JObject.Parse(File.ReadAllText(actualFile));
                    if (expected["dimension_names"] == null)
                    {
                        actual.Remove("dimension_names");
                    }

                    Assert.IsTrue(JToken.DeepEquals(expected, actual), $"The metadata of '{key}' differs.\nExpected: {expected}\nActual: {actual}");
                }
                else
                {
                    CollectionAssert.AreEqual(File.ReadAllBytes(expectedFile), File.ReadAllBytes(actualFile), $"The chunk '{key}' differs.");
                }
            }
        }

        static string GetRelativePath(string basePath, string path)
        {
            return path.Substring(basePath.Length).TrimStart(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');
        }
    }
}
