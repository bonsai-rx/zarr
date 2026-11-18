namespace Bonsai.Zarr
{
    /// <summary>
    /// Represents the chunking and creation options of an array written by
    /// <see cref="ZarrArrayWriter"/>.
    /// </summary>
    internal sealed class ZarrArraySettings
    {
        /// <summary>
        /// Gets or sets the number of rows stored in each chunk along the first dimension, or
        /// <see langword="null"/> to store every row in a single chunk.
        /// </summary>
        public int? ChunkLength { get; set; }

        /// <summary>
        /// Gets or sets the name of each dimension of the array, or <see langword="null"/> to
        /// derive the names from the array name.
        /// </summary>
        public string[]? DimensionNames { get; set; }

        /// <summary>
        /// Gets or sets a value specifying whether to replace an existing array at the same path.
        /// </summary>
        public bool Overwrite { get; set; }
    }
}
