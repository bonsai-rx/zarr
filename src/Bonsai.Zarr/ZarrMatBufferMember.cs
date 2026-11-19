using System;
using OpenCV.Net;

namespace Bonsai.Zarr
{
    /// <summary>
    /// Represents a matrix member that holds a buffer of samples, with one row per channel and one
    /// column per sample, written as one row per sample of a two-dimensional array.
    /// </summary>
    /// <typeparam name="TSource">The type of the elements in the sequence.</typeparam>
    internal sealed class ZarrMatBufferMember<TSource> : ZarrMember<TSource>
    {
        static readonly ZarrDataType[] DepthDataTypes;
        readonly Func<TSource, Mat> selector;

        static ZarrMatBufferMember()
        {
            DepthDataTypes = new ZarrDataType[(int)Depth.F64 + 1];
            foreach (var depth in new[] { Depth.U8, Depth.S8, Depth.U16, Depth.S16, Depth.S32, Depth.F32, Depth.F64 })
            {
                DepthDataTypes[(int)depth] = ZarrDataType.FromType(GetElementType(depth));
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ZarrMatBufferMember{TSource}"/> class.
        /// </summary>
        /// <param name="name">The path of the member relative to the element.</param>
        /// <param name="selector">A function that returns the member matrix of an element.</param>
        public ZarrMatBufferMember(string name, Func<TSource, Mat> selector)
            : base(name)
        {
            this.selector = selector;
        }

        Mat GetMatrix(TSource value)
        {
            var matrix = selector(value) ?? throw new InvalidOperationException($"{GetDisplayName()} is null.");
            if (matrix.Channels != 1)
            {
                throw new InvalidOperationException($"{GetDisplayName()} has {matrix.Channels} channels, but only single-channel matrices are supported.");
            }

            return matrix;
        }

        static Type GetElementType(Depth depth)
        {
            return depth switch
            {
                Depth.U8 => typeof(byte),
                Depth.S8 => typeof(sbyte),
                Depth.U16 => typeof(ushort),
                Depth.S16 => typeof(short),
                Depth.S32 => typeof(int),
                Depth.F32 => typeof(float),
                Depth.F64 => typeof(double),
                _ => throw new NotSupportedException($"The matrix depth '{depth}' has no corresponding Zarr data type.")
            };
        }

        static ZarrDataType GetDataType(Depth depth)
        {
            var index = (int)depth;
            if (index < 0 || index >= DepthDataTypes.Length)
            {
                throw new NotSupportedException($"The matrix depth '{depth}' has no corresponding Zarr data type.");
            }

            return DepthDataTypes[index];
        }

        /// <inheritdoc/>
        public override ZarrDataType GetDataType(TSource value)
        {
            return GetDataType(GetMatrix(value).Depth);
        }

        /// <inheritdoc/>
        public override int[] GetRowShape(TSource value) => new[] { GetMatrix(value).Rows };

        /// <inheritdoc/>
        public override int GetRowCount(TSource value) => GetMatrix(value).Cols;

        /// <inheritdoc/>
        public override void Validate(ZarrDataType dataType, int rowLength, TSource value)
        {
            var matrix = GetMatrix(value);
            var matrixDataType = GetDataType(matrix.Depth);
            if (matrixDataType.Name != dataType.Name)
            {
                throw new InvalidOperationException(
                    $"{GetDisplayName()} has elements of type {matrixDataType.Name} but the array holds {dataType.Name}.");
            }

            if (matrix.Rows != rowLength)
            {
                throw new InvalidOperationException(
                    $"{GetDisplayName()} has {matrix.Rows} rows but the array expects {rowLength} channels per sample.");
            }
        }

        /// <inheritdoc/>
        public override void Write(ZarrArrayWriter array, TSource value)
        {
            array.WriteColumns(GetMatrix(value));
        }
    }
}
