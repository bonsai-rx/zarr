using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bonsai.Zarr
{
    /// <summary>
    /// Provides methods for creating nodes and writing keys in a Zarr store backed by a local
    /// directory.
    /// </summary>
    internal static class ZarrStore
    {
        /// <summary>
        /// The name of the metadata document of every Zarr node.
        /// </summary>
        public const string MetadataKey = "zarr.json";

        /// <summary>
        /// The prefix of every chunk key under the default chunk key encoding.
        /// </summary>
        public const string ChunkKeyPrefix = "c";

        const string TemporarySuffix = ".tmp";
        const int MaxRetries = 8;
        static readonly Encoding MetadataEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        /// <summary>
        /// Splits a node path into its validated node names.
        /// </summary>
        /// <param name="path">
        /// A path relative to the store root, with node names separated by forward slashes.
        /// </param>
        /// <returns>The sequence of node names from the store root to the node.</returns>
        /// <exception cref="ArgumentException">
        /// The path is empty, resembles a file system path, or contains a node name that is not
        /// valid in a Zarr hierarchy.
        /// </exception>
        public static string[] GetNodeNames(string? path)
        {
            path ??= string.Empty;
            if (path.IndexOfAny(new[] { '\\', ':' }) >= 0)
            {
                throw new ArgumentException(
                    $"The name '{path}' resembles a file system path. Specify the store folder " +
                    "as the path, and the array or group inside the store as the name.",
                    nameof(path));
            }

            var names = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (names.Length == 0)
            {
                throw new ArgumentException("A node path must contain at least one node name.", nameof(path));
            }

            foreach (var name in names)
            {
                if (name.Trim('.').Length == 0 ||
                    name.StartsWith("__", StringComparison.Ordinal) ||
                    name == MetadataKey ||
                    name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    throw new ArgumentException($"The node name '{name}' is not valid in a Zarr hierarchy.", nameof(path));
                }
            }

            return names;
        }

        /// <summary>
        /// Ensures a group exists at the store root and at every node path in the sequence.
        /// </summary>
        /// <param name="storePath">The directory of the store.</param>
        /// <param name="groupNames">The node names of the groups below the store root.</param>
        /// <returns>The directory of the innermost group.</returns>
        /// <exception cref="IOException">A node along the path exists and is not a group.</exception>
        public static string EnsureGroups(string storePath, IEnumerable<string> groupNames)
        {
            var directory = storePath;
            EnsureGroup(directory);
            foreach (var name in groupNames)
            {
                directory = Path.Combine(directory, name);
                EnsureGroup(directory);
            }

            return directory;
        }

        static void EnsureGroup(string directory)
        {
            var metadataPath = Path.Combine(directory, MetadataKey);
            var nodeType = GetNodeType(directory);
            if (nodeType == null)
            {
                Directory.CreateDirectory(directory);
                WriteMetadata(metadataPath, ZarrMetadata.CreateGroup());
            }
            else if (nodeType != ZarrMetadata.GroupNodeType)
            {
                throw new IOException($"The Zarr node at '{directory}' is not a group.");
            }
        }

        /// <summary>
        /// Ensures no Zarr node exists in the specified directory, deleting an existing array or
        /// group, and everything it holds, if overwriting is enabled.
        /// </summary>
        /// <param name="directory">The directory of the node.</param>
        /// <param name="overwrite">
        /// <see langword="true"/> to delete an existing node; otherwise, <see langword="false"/>.
        /// </param>
        /// <exception cref="IOException">
        /// A node exists in the directory and overwriting is not enabled.
        /// </exception>
        public static void PrepareNode(string directory, bool overwrite)
        {
            var existingType = GetNodeType(directory);
            if (existingType == null) return;
            if (!overwrite)
            {
                throw new IOException($"A Zarr {existingType} already exists at '{directory}'.");
            }

            Directory.Delete(directory, recursive: true);
        }

        /// <summary>
        /// Returns the type of the Zarr node stored in the specified directory.
        /// </summary>
        /// <param name="directory">The directory of the node.</param>
        /// <returns>
        /// The value of the <c>node_type</c> field of the node metadata, an empty string if the
        /// metadata document has no readable node type, or <see langword="null"/> if the directory
        /// has no metadata document.
        /// </returns>
        public static string? GetNodeType(string directory)
        {
            var metadataPath = Path.Combine(directory, MetadataKey);
            if (!File.Exists(metadataPath)) return null;

            var text = Retry(() =>
            {
                using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, MetadataEncoding);
                return reader.ReadToEnd();
            });

            try
            {
                return (JObject.Parse(text)["node_type"] as JValue)?.Value as string ?? string.Empty;
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Returns the file path of a chunk of an array chunked only along its first dimension, under
        /// the default chunk key encoding.
        /// </summary>
        /// <param name="arrayDirectory">The directory of the array.</param>
        /// <param name="chunkIndex">The index of the chunk along the first dimension.</param>
        /// <param name="rowRank">The number of dimensions of a single row.</param>
        /// <returns>The file path of the chunk.</returns>
        public static string GetChunkPath(string arrayDirectory, long chunkIndex, int rowRank)
        {
            var key = new StringBuilder(ChunkKeyPrefix);
            key.Append(Path.DirectorySeparatorChar).Append(chunkIndex);
            for (int i = 0; i < rowRank; i++)
            {
                key.Append(Path.DirectorySeparatorChar).Append('0');
            }

            return Path.Combine(arrayDirectory, key.ToString());
        }

        /// <summary>
        /// Writes a metadata document to the specified key, replacing any existing document atomically.
        /// </summary>
        /// <param name="path">The file path of the metadata document.</param>
        /// <param name="document">The JSON object to write.</param>
        public static void WriteMetadata(string path, JObject document)
        {
            using var text = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
            using (var writer = new JsonTextWriter(text) { Formatting = Formatting.Indented, Indentation = 2 })
            {
                document.WriteTo(writer);
            }

            WriteFile(path, MetadataEncoding.GetBytes(text.ToString()));
        }

        /// <summary>
        /// Writes the contents of a file to a temporary file and then moves it into place,
        /// replacing any existing file.
        /// </summary>
        /// <remarks>
        /// Replacing a file that another writer or process holds open is retried with increasing
        /// delays, for up to a quarter of a second, before the error is raised.
        /// </remarks>
        /// <param name="path">The path of the file to write.</param>
        /// <param name="contents">The bytes to write to the file.</param>
        public static void WriteFile(string path, byte[] contents)
        {
            var temporaryPath = $"{path}.{Guid.NewGuid():N}{TemporarySuffix}";
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents, 0, contents.Length);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                Retry(() => MoveFile(temporaryPath, path));
            }
            catch
            {
                File.Delete(temporaryPath);
                throw;
            }
        }

        static void MoveFile(string sourcePath, string destinationPath)
        {
#if NET
            File.Move(sourcePath, destinationPath, overwrite: true);
#else
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                if (!NativeMethods.MoveFileEx(sourcePath, destinationPath, NativeMethods.MoveFileReplaceExisting))
                {
                    throw new IOException(
                        $"Unable to move '{sourcePath}' to '{destinationPath}'.",
                        Marshal.GetHRForLastWin32Error());
                }
            }
            else if (File.Exists(destinationPath))
            {
                File.Replace(sourcePath, destinationPath, destinationBackupFileName: null);
            }
            else File.Move(sourcePath, destinationPath);
#endif
        }

        static void Retry(Action operation)
        {
            Retry(() =>
            {
                operation();
                return true;
            });
        }

        static T Retry<T>(Func<T> operation)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return operation();
                }
                catch (Exception ex) when (attempt < MaxRetries && ex is IOException and not (FileNotFoundException or DirectoryNotFoundException) or UnauthorizedAccessException)
                {
                    Thread.Sleep(1 << attempt);
                }
            }
        }
    }
}
