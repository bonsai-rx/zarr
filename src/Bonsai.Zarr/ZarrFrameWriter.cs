using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;

namespace Bonsai.Zarr
{
    /// <summary>
    /// Represents a writer that appends the members of each element in a sequence to a single
    /// Zarr array, or to a group with one array per member.
    /// </summary>
    /// <remarks>
    /// Each element is checked when it is written and then queued by reference. The arrays are
    /// created, written and closed on a dedicated thread. Elements must be written from one thread
    /// at a time.
    /// </remarks>
    /// <typeparam name="TSource">The type of the elements in the sequence.</typeparam>
    internal sealed class ZarrFrameWriter<TSource>
    {
        readonly string storePath;
        readonly string path;
        readonly ZarrMember<TSource>[] members;
        readonly Func<TSource, int>? getBufferLength;
        readonly int? chunkLength;
        readonly bool overwrite;
        readonly ZarrBackgroundWriter<TSource> writer;
        ZarrDataType[]? dataTypes;
        int[][]? rowShapes;
        int[]? rowLengths;
        ZarrArrayWriter[]? arrays;

        /// <summary>
        /// Initializes a new instance of the <see cref="ZarrFrameWriter{TSource}"/> class and
        /// starts its writer thread. No node is created until the first element is written.
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
        /// A function that returns the number of rows added to every array by each element, or
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
        /// <param name="onError">
        /// The action called on the writer thread with the first error raised while creating,
        /// writing or closing the arrays.
        /// </param>
        /// <param name="onClosed">The action called last on the writer thread.</param>
        public ZarrFrameWriter(
            string storePath,
            string path,
            ZarrMember<TSource>[] members,
            Func<TSource, int>? getBufferLength,
            int? chunkLength,
            bool overwrite,
            Action<Exception> onError,
            Action onClosed)
        {
            this.storePath = storePath;
            this.path = path;
            this.members = members;
            this.getBufferLength = getBufferLength;
            this.chunkLength = chunkLength;
            this.overwrite = overwrite;
            writer = new ZarrBackgroundWriter<TSource>(WriteMembers, Flush, Close, onError, onClosed);
        }

        bool IsSingleArray => members.Length == 1 && members[0].Name.Length == 0;

        /// <summary>
        /// Checks the members of an element and queues it to be appended to the arrays. The first
        /// element determines the data type and row shape of each array.
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

            if (dataTypes == null)
            {
                dataTypes = members.Select(member => member.GetDataType(value)).ToArray();
                rowShapes = members.Select(member => member.GetRowShape(value)).ToArray();
                rowLengths = rowShapes.Select(shape => shape.Aggregate(1, (product, length) => product * length)).ToArray();
            }

            for (int i = 0; i < members.Length; i++)
            {
                members[i].Validate(dataTypes[i], rowLengths![i], value);
            }

            writer.Write(value);
        }

        /// <summary>
        /// Stops accepting elements, then closes the arrays once every queued element is written.
        /// </summary>
        public void Complete()
        {
            writer.Complete();
        }

        void WriteMembers(TSource value)
        {
            arrays ??= CreateArrays();
            for (int i = 0; i < members.Length; i++)
            {
                members[i].Write(arrays[i], value);
            }
        }

        void Flush()
        {
            if (arrays == null) return;
            foreach (var array in arrays)
            {
                array.Flush();
            }
        }

        void Close()
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

        ZarrArrayWriter[] CreateArrays()
        {
            if (IsSingleArray)
            {
                var settings = new ZarrArraySettings { ChunkLength = chunkLength, Overwrite = overwrite };
                return new[] { new ZarrArrayWriter(storePath, path, dataTypes![0], rowShapes![0], settings) };
            }

            var nodeNames = ZarrStore.GetNodeNames(path);
            var groupName = nodeNames[nodeNames.Length - 1];
            var groupDirectory = Path.Combine(new[] { storePath }.Concat(nodeNames).ToArray());
            ZarrStore.PrepareNode(groupDirectory, overwrite);

            var result = new List<ZarrArrayWriter>(members.Length);
            try
            {
                for (int i = 0; i < members.Length; i++)
                {
                    var member = members[i];
                    var rowShape = rowShapes![i];
                    var settings = new ZarrArraySettings
                    {
                        ChunkLength = chunkLength,
                        DimensionNames = new[] { $"{groupName}_dim_0" }
                            .Concat(rowShape.Select((_, dimension) => $"{member.Name}_dim_{dimension + 1}"))
                            .ToArray()
                    };
                    result.Add(new ZarrArrayWriter(storePath, $"{path}/{member.Name}", dataTypes![i], rowShape, settings));
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
    }
}
