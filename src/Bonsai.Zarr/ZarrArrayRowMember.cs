using System;
using System.Runtime.InteropServices;

namespace Bonsai.Zarr
{
    /// <summary>
    /// Represents an array member written as one row of a two-dimensional array, where every array
    /// has the same number of values.
    /// </summary>
    /// <typeparam name="TSource">The type of the elements in the sequence.</typeparam>
    /// <typeparam name="T">The type of the values in the member array.</typeparam>
    internal sealed class ZarrArrayRowMember<TSource, T> : ZarrMember<TSource> where T : unmanaged
    {
        readonly Func<TSource, T[]> selector;
        readonly ZarrDataType dataType;

        /// <summary>
        /// Initializes a new instance of the <see cref="ZarrArrayRowMember{TSource, T}"/> class.
        /// </summary>
        /// <param name="name">The path of the member relative to the element.</param>
        /// <param name="selector">A function that returns the member array of an element.</param>
        public ZarrArrayRowMember(string name, Func<TSource, T[]> selector)
            : base(name)
        {
            this.selector = selector;
            dataType = ZarrDataType.FromType(typeof(T));
        }

        T[] GetArray(TSource value)
        {
            return selector(value) ?? throw new InvalidOperationException($"{GetDisplayName()} is null.");
        }

        /// <inheritdoc/>
        public override ZarrDataType GetDataType(TSource value) => dataType;

        /// <inheritdoc/>
        public override int[] GetRowShape(TSource value) => new[] { GetArray(value).Length };

        /// <inheritdoc/>
        public override int GetRowCount(TSource value) => 1;

        /// <inheritdoc/>
        public override void Validate(ZarrArrayWriter array, TSource value)
        {
            var length = GetArray(value).Length;
            if (length != array.RowLength)
            {
                throw new InvalidOperationException(
                    $"{GetDisplayName()} has {length} values but the array expects {array.RowLength} values per row.");
            }
        }

        /// <inheritdoc/>
        public override void Write(ZarrArrayWriter array, TSource value)
        {
            array.Write(MemoryMarshal.AsBytes(GetArray(value).AsSpan()));
        }
    }
}
