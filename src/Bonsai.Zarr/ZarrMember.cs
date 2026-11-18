namespace Bonsai.Zarr
{
    /// <summary>
    /// Provides an abstract base class for writing one member of each element in a sequence to
    /// its own Zarr array.
    /// </summary>
    /// <typeparam name="TSource">The type of the elements in the sequence.</typeparam>
    internal abstract class ZarrMember<TSource>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ZarrMember{TSource}"/> class.
        /// </summary>
        /// <param name="name">
        /// The path of the member relative to the element, or an empty string if the member is the
        /// element itself.
        /// </param>
        protected ZarrMember(string name)
        {
            Name = name;
        }

        /// <summary>
        /// Gets the path of the member relative to the element, or an empty string if the member is
        /// the element itself.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Returns the data type of the array that holds the member values.
        /// </summary>
        /// <param name="value">The first element of the sequence.</param>
        /// <returns>The Zarr data type of the array elements.</returns>
        public abstract ZarrDataType GetDataType(TSource value);

        /// <summary>
        /// Returns the length of each dimension of a row in the array that holds the member values.
        /// </summary>
        /// <param name="value">The first element of the sequence.</param>
        /// <returns>The length of each dimension of a single row.</returns>
        public abstract int[] GetRowShape(TSource value);

        /// <summary>
        /// Returns the number of rows the member of the specified element adds to the array.
        /// </summary>
        /// <param name="value">An element of the sequence.</param>
        /// <returns>The number of rows to append.</returns>
        public abstract int GetRowCount(TSource value);

        /// <summary>
        /// Checks that the member of the specified element can be appended to the array.
        /// </summary>
        /// <param name="array">The writer of the array that holds the member values.</param>
        /// <param name="value">An element of the sequence.</param>
        /// <exception cref="System.InvalidOperationException">
        /// The member is missing or its shape or type differs from the array.
        /// </exception>
        public abstract void Validate(ZarrArrayWriter array, TSource value);

        /// <summary>
        /// Appends the member of the specified element to the array.
        /// </summary>
        /// <param name="array">The writer of the array that holds the member values.</param>
        /// <param name="value">An element of the sequence that passed validation.</param>
        public abstract void Write(ZarrArrayWriter array, TSource value);

        /// <summary>
        /// Returns a description of the member for use in error messages.
        /// </summary>
        /// <returns>The quoted member path, or a reference to the element itself.</returns>
        public string GetDisplayName()
        {
            return Name.Length == 0 ? "The element" : $"The member '{Name}'";
        }
    }
}
