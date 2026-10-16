using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RefractionStudio.Domain;

/// <summary>Strongly typed identifier backed by a <see cref="Guid"/>.</summary>
public interface IGuidId<TSelf> where TSelf : struct, IGuidId<TSelf>
{
    Guid Value { get; }
    static abstract TSelf From(Guid value);
}

/// <summary>Identifies one field record (one shot file, or one ensemble of a multi-record file).</summary>
[JsonConverter(typeof(GuidIdJsonConverter<RecordId>))]
public readonly record struct RecordId(Guid Value) : IGuidId<RecordId>
{
    public static RecordId New() => new(Guid.NewGuid());
    public static RecordId From(Guid value) => new(value);
    public override string ToString() => Value.ToString("N");
}

/// <summary>Identifies an embedded raw source file.</summary>
[JsonConverter(typeof(GuidIdJsonConverter<SourceFileId>))]
public readonly record struct SourceFileId(Guid Value) : IGuidId<SourceFileId>
{
    public static SourceFileId New() => new(Guid.NewGuid());
    public static SourceFileId From(Guid value) => new(value);
    public override string ToString() => Value.ToString("N");
}

/// <summary>Identifies a survey line (one wave type, one profile).</summary>
[JsonConverter(typeof(GuidIdJsonConverter<LineId>))]
public readonly record struct LineId(Guid Value) : IGuidId<LineId>
{
    public static LineId New() => new(Guid.NewGuid());
    public static LineId From(Guid value) => new(value);
    public override string ToString() => Value.ToString("N");
}

/// <summary>Identifies a receiver spread layout on a line.</summary>
[JsonConverter(typeof(GuidIdJsonConverter<SpreadId>))]
public readonly record struct SpreadId(Guid Value) : IGuidId<SpreadId>
{
    public static SpreadId New() => new(Guid.NewGuid());
    public static SpreadId From(Guid value) => new(value);
    public override string ToString() => Value.ToString("N");
}

/// <summary>Identifies a shot gather (the unit that is displayed and picked).</summary>
[JsonConverter(typeof(GuidIdJsonConverter<GatherId>))]
public readonly record struct GatherId(Guid Value) : IGuidId<GatherId>
{
    public static GatherId New() => new(Guid.NewGuid());
    public static GatherId From(Guid value) => new(value);
    public override string ToString() => Value.ToString("N");
}

/// <summary>Identifies an analysis scenario.</summary>
[JsonConverter(typeof(GuidIdJsonConverter<ScenarioId>))]
public readonly record struct ScenarioId(Guid Value) : IGuidId<ScenarioId>
{
    public static ScenarioId New() => new(Guid.NewGuid());
    public static ScenarioId From(Guid value) => new(value);
    public override string ToString() => Value.ToString("N");
}

/// <summary>Identifies a processing step inside a pipeline.</summary>
[JsonConverter(typeof(GuidIdJsonConverter<StepId>))]
public readonly record struct StepId(Guid Value) : IGuidId<StepId>
{
    public static StepId New() => new(Guid.NewGuid());
    public static StepId From(Guid value) => new(value);
    public override string ToString() => Value.ToString("N");
}

/// <summary>Identifies a named project snapshot.</summary>
[JsonConverter(typeof(GuidIdJsonConverter<SnapshotId>))]
public readonly record struct SnapshotId(Guid Value) : IGuidId<SnapshotId>
{
    public static SnapshotId New() => new(Guid.NewGuid());
    public static SnapshotId From(Guid value) => new(value);
    public override string ToString() => Value.ToString("N");
}

/// <summary>A raw trace: a channel of one record.</summary>
[JsonConverter(typeof(TraceRefJsonConverter))]
public readonly record struct TraceRef(RecordId Record, int Channel)
{
    public override string ToString() => $"{Record}:{Channel.ToString(CultureInfo.InvariantCulture)}";

    public static TraceRef Parse(string text)
    {
        var colon = text.LastIndexOf(':');
        if (colon <= 0) throw new FormatException($"'{text}' is not a trace reference.");
        return new TraceRef(new RecordId(Guid.Parse(text.AsSpan(0, colon))),
            int.Parse(text.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture));
    }
}

/// <summary>A pickable trace: a channel of a shot gather.</summary>
[JsonConverter(typeof(PickKeyJsonConverter))]
public readonly record struct PickKey(GatherId Gather, int Channel)
{
    public override string ToString() => $"{Gather}:{Channel.ToString(CultureInfo.InvariantCulture)}";

    public static PickKey Parse(string text)
    {
        var colon = text.LastIndexOf(':');
        if (colon <= 0) throw new FormatException($"'{text}' is not a pick key.");
        return new PickKey(new GatherId(Guid.Parse(text.AsSpan(0, colon))),
            int.Parse(text.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture));
    }
}

/// <summary>Serialises a Guid-backed id as a plain string, also usable as a dictionary key.</summary>
public sealed class GuidIdJsonConverter<T> : JsonConverter<T> where T : struct, IGuidId<T>
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => T.From(Guid.Parse(reader.GetString() ?? throw new JsonException("Identifier is null.")));

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value.ToString("N"));

    public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Read(ref reader, typeToConvert, options);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.Value.ToString("N"));
}

/// <summary>Serialises <see cref="PickKey"/> as "gather:channel".</summary>
public sealed class PickKeyJsonConverter : JsonConverter<PickKey>
{
    public override PickKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => PickKey.Parse(reader.GetString() ?? throw new JsonException("Pick key is null."));

    public override void Write(Utf8JsonWriter writer, PickKey value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());

    public override PickKey ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Read(ref reader, typeToConvert, options);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, PickKey value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.ToString());
}

/// <summary>Serialises <see cref="TraceRef"/> as "record:channel".</summary>
public sealed class TraceRefJsonConverter : JsonConverter<TraceRef>
{
    public override TraceRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => TraceRef.Parse(reader.GetString() ?? throw new JsonException("Trace reference is null."));

    public override void Write(Utf8JsonWriter writer, TraceRef value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());

    public override TraceRef ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Read(ref reader, typeToConvert, options);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, TraceRef value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.ToString());
}
