using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RefractionStudio.Domain;

/// <summary>Shared JSON settings for project files (camelCase, string enums, full double precision).</summary>
public static class DomainJson
{
    public static JsonSerializerOptions Options { get; } = Create(indented: true);

    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        o.Converters.Add(new ImmutableArrayConverterFactory());
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }
}

/// <summary>
/// Serialises <see cref="ImmutableArray{T}"/>, writing an uninitialised (default) array as [] and
/// reading null as an empty array, so that records with optional array parameters always round-trip.
/// </summary>
public sealed class ImmutableArrayConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(ImmutableArray<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(typeof(ImmutableArrayConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;

    private sealed class ImmutableArrayConverter<T> : JsonConverter<ImmutableArray<T>>
    {
        public override bool HandleNull => true;

        public override ImmutableArray<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return [];
            var list = JsonSerializer.Deserialize<List<T>>(ref reader, options);
            return list is null ? [] : [.. list];
        }

        public override void Write(Utf8JsonWriter writer, ImmutableArray<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            if (!value.IsDefault)
                foreach (var item in value) JsonSerializer.Serialize(writer, item, options);
            writer.WriteEndArray();
        }
    }
}
