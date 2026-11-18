using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;

namespace Bonsai.Zarr
{
    /// <summary>
    /// Represents a writer that appends the members of each element in a sequence to a single
    /// Zarr array, or to a group with one array per member, so every array has the same length.
    /// </summary>
    /// <typeparam name="TSource">The type of the elements in the sequence.</typeparam>
    internal sealed class ZarrFrameWriter<TSource> : IDisposable
    {
        readonly string storePath;
        readonly string path;
        readonly ZarrMember<TSource>[] members;
        readonly Func<TSource, int>? getBufferLength;
        readonly int? chunkLength;
        readonly bool overwrite;
        ZarrArrayWriter[]? arrays;

        /// <summary>
        /// Initializes a new instance of the <see cref="ZarrFrameWriter{TSource}"/> class. No
        /// node is created until the first element is written.
        /// </summary>
        /// <param name="storePath">The directory of the store.</param>
        /// <param name="path">
        /// The path of the array, or of the group that holds the member arrays, relative to the
        /// store root.
        /// </param>
        /// <param name="members">
        /// The members to write. A single member with an empty name is written to an array at
        /// <paramref name="path"/>; otherwise, each member is written to an array in a group.
        /// </param>
        /// <param name="getBufferLength">
        /// A function that returns the number of rows each element adds to every array, or
        /// <see langword="null"/> if each element adds a single row.
        /// </param>
        /// <param name="chunkLength">
        /// The number of rows stored in each chunk, or <see langword="null"/> to store every row of
        /// each array in a single file.
        /// </param>
        /// <param name="overwrite">
        /// <see langword="true"/> to replace an existing node at the same path; otherwise,
        /// <see langword="false"/>.
        /// </param>
        public ZarrFrameWriter(
            string storePath,
            string path,
            ZarrMember<TSource>[] members,
            Func<TSource, int>? getBufferLength,
            int? chunkLength,
            bool overwrite)
        {
            this.storePath = storePath;
            this.path = path;
            this.members = members;
            this.getBufferLength = getBufferLength;
            this.chunkLength = chunkLength;
            this.overwrite = overwrite;
        }

        bool IsSingleArray => members.Length == 1 && members[0].Name.Length == 0;

        /// <summary>
        /// Appends the members of an element to their arrays. The first element determines the
        /// data type and row length of each array.
        /// </summary>
        /// <param name="value">The element to write.</param>
        /// <exception cref="InvalidOperationException">
        /// The element is null, or one of its members does not match the shape of its array or
        /// the number of rows in the buffer.
        /// </exception>
        public void Write(TSource value)
        {
            if (value is null)
            {
                throw new InvalidOperationException("Null elements cannot be written to a Zarr store.");
            }

            var rowCount = getBufferLength?.Invoke(value) ?? 1;
            foreach (var member in members)
            {
                var memberRowCount = member.GetRowCount(value);
                if (memberRowCount != rowCount)
                {
                    throw new InvalidOperationException(
                        $"{member.GetDisplayName()} has {memberRowCount} samples but the buffer has {rowCount}.");
                }
            }

            arrays ??= CreateArrays(value);
            for (int i = 0; i < members.Length; i++)
            {
                members[i].Validate(arrays[i], value);
            }

            for (int i = 0; i < members.Length; i++)
            {
                members[i].Write(arrays[i], value);
            }
        }

        ZarrArrayWriter[] CreateArrays(TSource value)
        {
            if (IsSingleArray)
            {
                var member = members[0];
                var settings = new ZarrArraySettings { ChunkLength = chunkLength, Overwrite = overwrite };
                return new[] { new ZarrArrayWriter(storePath, path, member.GetDataType(value), member.GetRowShape(value), settings) };
            }

            var nodeNames = ZarrStore.GetNodeNames(path);
            var groupName = nodeNames[nodeNames.Length - 1];
            var groupDirectory = Path.Combine(new[] { storePath }.Concat(nodeNames).ToArray());
            ZarrStore.PrepareNode(groupDirectory, overwrite);

            var result = new List<ZarrArrayWriter>(members.Length);
            try
            {
                foreach (var member in members)
                {
                    var rowShape = member.GetRowShape(value);
                    var settings = new ZarrArraySettings
                    {
                        ChunkLength = chunkLength,
                        DimensionNames = new[] { $"{groupName}_dim_0" }
                            .Concat(rowShape.Select((_, i) => $"{member.Name}_dim_{i + 1}"))
                            .ToArray()
                    };
                    result.Add(new ZarrArrayWriter(storePath, $"{path}/{member.Name}", member.GetDataType(value), rowShape, settings));
                }
            }
            catch
            {
                foreach (var array in result)
                {
                    array.Dispose();
                }

                throw;
            }

            return result.ToArray();
        }

        /// <summary>
        /// Pads the last chunk of every array, writes the final shape of each array, and releases
        /// the resources used by the writer.
        /// </summary>
        public void Dispose()
        {
            if (arrays == null) return;
            Exception? error = null;
            foreach (var array in arrays)
            {
                try
                {
                    array.Dispose();
                }
                catch (Exception ex)
                {
                    error ??= ex;
                }
            }

            if (error != null)
            {
                ExceptionDispatchInfo.Capture(error).Throw();
            }
        }
    }
}
