using System;
using System.Runtime.InteropServices;

namespace Bonsai.Zarr
{
    /// <summary>
    /// Represents a member that holds a single value per element, written as one row of a
    /// one-dimensional array.
    /// </summary>
    /// <typeparam name="TSource">The type of the elements in the sequence.</typeparam>
    /// <typeparam name="T">The type of the member value.</typeparam>
    internal sealed class ZarrScalarMember<TSource, T> : ZarrMember<TSource> where T : unmanaged
    {
        readonly Func<TSource, T> selector;
        readonly ZarrDataType dataType;

        /// <summary>
        /// Initializes a new instance of the <see cref="ZarrScalarMember{TSource, T}"/> class.
        /// </summary>
        /// <param name="name">The path of the member relative to the element.</param>
        /// <param name="selector">A function that returns the member value of an element.</param>
        public ZarrScalarMember(string name, Func<TSource, T> selector)
            : base(name)
        {
            this.selector = selector;
            dataType = ZarrDataType.FromType(typeof(T));
        }

        /// <inheritdoc/>
        public override ZarrDataType GetDataType(TSource value) => dataType;

        /// <inheritdoc/>
        public override int[] GetRowShape(TSource value) => Array.Empty<int>();

        /// <inheritdoc/>
        public override int GetRowCount(TSource value) => 1;

        /// <inheritdoc/>
        public override void Validate(ZarrDataType dataType, int rowLength, TSource value)
        {
        }

        /// <inheritdoc/>
        public override void Write(ZarrArrayWriter array, TSource value)
        {
            Span<T> row = stackalloc T[1];
            row[0] = selector(value);
            array.Write(MemoryMarshal.AsBytes(row));
        }
    }
}
