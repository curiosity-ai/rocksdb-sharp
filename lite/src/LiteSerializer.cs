using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RocksDbSharp.Lite
{
    /// <summary>
    /// Pluggable document serializer. The library does not assume any specific binary format.
    /// </summary>
    public interface ILiteSerializer
    {
        byte[] Serialize<T>(T value);
        T Deserialize<T>(ReadOnlySpan<byte> bytes);
    }

    /// <summary>
    /// Default serializer based on System.Text.Json. Suitable for plain DTOs.
    /// </summary>
    public sealed class JsonLiteSerializer : ILiteSerializer
    {
        public static JsonLiteSerializer Default { get; } = new JsonLiteSerializer();

        private readonly JsonSerializerOptions _options;

        public JsonLiteSerializer() : this(BuildDefaultOptions()) { }

        public JsonLiteSerializer(JsonSerializerOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        private static JsonSerializerOptions BuildDefaultOptions() => new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            IncludeFields = false,
            PropertyNameCaseInsensitive = true,
        };

        public byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, _options);

        public T Deserialize<T>(ReadOnlySpan<byte> bytes) => JsonSerializer.Deserialize<T>(bytes, _options)!;
    }
}
