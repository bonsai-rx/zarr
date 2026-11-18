using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Bonsai.Zarr
{
    /// <summary>
    /// Provides methods for creating the metadata documents of Zarr version 3 nodes.
    /// </summary>
    internal static class ZarrMetadata
    {
        /// <summary>
        /// The node type of a Zarr group.
        /// </summary>
        public const string GroupNodeType = "group";

        /// <summary>
        /// The node type of a Zarr array.
        /// </summary>
        public const string ArrayNodeType = "array";

        const int ZarrFormat = 3;

        /// <summary>
        /// Creates the metadata document of a group with no attributes.
        /// </summary>
        /// <returns>The group metadata document.</returns>
        public static JObject CreateGroup()
        {
            return new JObject
            {
                { "attributes", new JObject() },
                { "zarr_format", ZarrFormat },
                { "node_type", GroupNodeType }
            };
        }

        /// <summary>
        /// Creates the metadata document of an array with a regular chunk grid, the default chunk
        /// key encoding, a single codec and no attributes.
        /// </summary>
        /// <param name="shape">The length of each dimension of the array.</param>
        /// <param name="dataType">The name of the Zarr data type of the array elements.</param>
        /// <param name="chunkShape">The length of each dimension of a chunk.</param>
        /// <param name="fillValue">The JSON representation of the fill value.</param>
        /// <param name="codec">The metadata of the codec that serializes the array elements.</param>
        /// <param name="dimensionNames">The name of each dimension of the array.</param>
        /// <returns>The array metadata document.</returns>
        public static JObject CreateArray(
            IEnumerable<long> shape,
            string dataType,
            IEnumerable<long> chunkShape,
            JToken fillValue,
            JObject codec,
            IEnumerable<string> dimensionNames)
        {
            return new JObject
            {
                { "shape", new JArray(shape) },
                { "data_type", dataType },
                { "chunk_grid", new JObject
                    {
                        { "name", "regular" },
                        { "configuration", new JObject { { "chunk_shape", new JArray(chunkShape) } } }
                    }
                },
                { "chunk_key_encoding", new JObject
                    {
                        { "name", "default" },
                        { "configuration", new JObject { { "separator", "/" } } }
                    }
                },
                { "fill_value", fillValue },
                { "codecs", new JArray(codec) },
                { "attributes", new JObject() },
                { "dimension_names", new JArray(dimensionNames) },
                { "zarr_format", ZarrFormat },
                { "node_type", ArrayNodeType },
                { "storage_transformers", new JArray() }
            };
        }

        /// <summary>
        /// Creates the metadata of the <c>bytes</c> codec with little-endian byte order.
        /// </summary>
        /// <param name="elementSize">The size of each array element, in bytes.</param>
        /// <returns>
        /// The codec metadata, with the byte order omitted for single-byte element types.
        /// </returns>
        public static JObject CreateBytesCodec(int elementSize)
        {
            var codec = new JObject { { "name", "bytes" } };
            if (elementSize > 1)
            {
                codec.Add("configuration", new JObject { { "endian", "little" } });
            }

            return codec;
        }
    }
}
