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
        readonly Func<TSource, Mat> selector;

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

        /// <inheritdoc/>
        public override ZarrDataType GetDataType(TSource value)
        {
            return ZarrDataType.FromType(GetElementType(GetMatrix(value).Depth));
        }

        /// <inheritdoc/>
        public override int[] GetRowShape(TSource value) => new[] { GetMatrix(value).Rows };

        /// <inheritdoc/>
        public override int GetRowCount(TSource value) => GetMatrix(value).Cols;

        /// <inheritdoc/>
        public override void Validate(ZarrArrayWriter array, TSource value)
        {
            var matrix = GetMatrix(value);
            var dataType = ZarrDataType.FromType(GetElementType(matrix.Depth));
            if (dataType.Name != array.DataType.Name)
            {
                throw new InvalidOperationException(
                    $"{GetDisplayName()} has elements of type {dataType.Name} but the array holds {array.DataType.Name}.");
            }

            if (matrix.Rows != array.RowLength)
            {
                throw new InvalidOperationException(
                    $"{GetDisplayName()} has {matrix.Rows} rows but the array expects {array.RowLength} channels per sample.");
            }
        }

        /// <inheritdoc/>
        public override void Write(ZarrArrayWriter array, TSource value)
        {
            var matrix = GetMatrix(value);
            array.WriteColumns(matrix.Data, matrix.Step, matrix.Rows, matrix.Cols);
        }
    }
}
