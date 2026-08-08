using System;

namespace RocksDbSharp.Lite
{
    /// <summary>
    /// Marks a property as the document identifier for a <see cref="LiteCollection{T}"/>.
    /// If absent, the collection looks for a public property literally named "Id".
    /// Supported id types: <see cref="long"/>, <see cref="int"/>, <see cref="Guid"/>, <see cref="string"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class LiteIdAttribute : Attribute
    {
        /// <summary>
        /// When true (default) and the id is a numeric type, the collection assigns auto-increment values on Insert
        /// when the supplied id is the type's default (0). Guid ids are auto-assigned with <see cref="Guid.NewGuid"/>.
        /// </summary>
        public bool AutoId { get; set; } = true;
    }

    /// <summary>
    /// Excludes a property from serialization.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class LiteIgnoreAttribute : Attribute { }
}
