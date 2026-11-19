using System;
using System.Runtime.InteropServices;

namespace Bonsai.Zarr
{
    /// <summary>
    /// Represents an array member that holds a buffer of samples, with each value written as one
    /// row of a one-dimensional array.
    /// </summary>
    /// <typeparam name="TSource">The type of the elements in the sequence.</typeparam>
    /// <typeparam name="T">The type of the values in the member array.</typeparam>
    internal sealed class ZarrArrayBufferMember<TSource, T> : ZarrMember<TSource> where T : unmanaged
    {
        readonly Func<TSource, T[]> selector;
        readonly ZarrDataType dataType;

        /// <summary>
        /// Initializes a new instance of the <see cref="ZarrArrayBufferMember{TSource, T}"/> class.
        /// </summary>
        /// <param name="name">The path of the member relative to the element.</param>
        /// <param name="selector">A function that returns the member array of an element.</param>
        public ZarrArrayBufferMember(string name, Func<TSource, T[]> selector)
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
        public override int[] GetRowShape(TSource value) => Array.Empty<int>();

        /// <inheritdoc/>
        public override int GetRowCount(TSource value) => GetArray(value).Length;

        /// <inheritdoc/>
        public override void Validate(ZarrDataType dataType, int rowLength, TSource value)
        {
        }

        /// <inheritdoc/>
        public override void Write(ZarrArrayWriter array, TSource value)
        {
            array.Write(MemoryMarshal.AsBytes(GetArray(value).AsSpan()));
        }
    }
}
