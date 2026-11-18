using System;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;

namespace Bonsai.Zarr
{
    /// <summary>
    /// Represents a Zarr core data type together with its size and the JSON representation of its
    /// fill value.
    /// </summary>
    internal sealed class ZarrDataType
    {
        ZarrDataType(string name, int elementSize, JToken fillValue)
        {
            Name = name;
            ElementSize = elementSize;
            FillValue = fillValue;
        }

        /// <summary>
        /// Gets the name of the data type.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the size of each element, in bytes.
        /// </summary>
        public int ElementSize { get; }

        /// <summary>
        /// Gets the JSON representation of the fill value, which is the default value of the type.
        /// </summary>
        public JToken FillValue { get; }

        /// <summary>
        /// Returns a value indicating whether the specified type has a corresponding Zarr data type.
        /// </summary>
        /// <param name="type">The type to test.</param>
        /// <returns>
        /// <see langword="true"/> if <paramref name="type"/> is <see cref="bool"/>, a signed or
        /// unsigned integer type of 8 to 64 bits, <see cref="float"/> or <see cref="double"/>;
        /// otherwise, <see langword="false"/>.
        /// </returns>
        public static bool IsSupported(Type type)
        {
            return !type.IsEnum && GetName(type) != null;
        }

        /// <summary>
        /// Returns the Zarr data type corresponding to the specified type.
        /// </summary>
        /// <param name="type">A boolean, integer or floating-point type.</param>
        /// <returns>The corresponding Zarr data type.</returns>
        /// <exception cref="NotSupportedException">
        /// <paramref name="type"/> has no corresponding Zarr data type.
        /// </exception>
        public static ZarrDataType FromType(Type type)
        {
            var name = type.IsEnum ? null : GetName(type);
            if (name == null)
            {
                throw new NotSupportedException($"The type '{type}' has no corresponding Zarr data type.");
            }

            var fillValue = JToken.FromObject(Activator.CreateInstance(type)!);
            return new ZarrDataType(name, Marshal.SizeOf(type == typeof(bool) ? typeof(byte) : type), fillValue);
        }

        static string? GetName(Type type)
        {
            return Type.GetTypeCode(type) switch
            {
                TypeCode.Boolean => "bool",
                TypeCode.SByte => "int8",
                TypeCode.Int16 => "int16",
                TypeCode.Int32 => "int32",
                TypeCode.Int64 => "int64",
                TypeCode.Byte => "uint8",
                TypeCode.UInt16 => "uint16",
                TypeCode.UInt32 => "uint32",
                TypeCode.UInt64 => "uint64",
                TypeCode.Single => "float32",
                TypeCode.Double => "float64",
                _ => null
            };
        }
    }
}
