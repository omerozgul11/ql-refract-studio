namespace RefractionStudio.Domain;

/// <summary>
/// The outcome of a scientific calculation: either a value or the reason it could not be computed
/// reliably. The engine never substitutes a made-up number for a missing one.
/// </summary>
public sealed record Computed<T>
{
    private Computed(bool hasValue, T? value, string? reason, ImmutableArray<string> notes)
    {
        HasValue = hasValue;
        Value = value;
        Reason = reason;
        Notes = notes;
    }

    public bool HasValue { get; }

    /// <summary>The value; only meaningful when <see cref="HasValue"/> is true.</summary>
    public T? Value { get; }

    /// <summary>Why no value is available (null when <see cref="HasValue"/>).</summary>
    public string? Reason { get; }

    /// <summary>Qualifications that apply to a value (e.g. "EXTRAPOLATED").</summary>
    public ImmutableArray<string> Notes { get; }

    public static Computed<T> Ok(T value, params string[] notes) => new(true, value, null, [.. notes]);

    public static Computed<T> Unavailable(string reason) => new(false, default, reason, []);

    public T GetValueOrThrow() => HasValue ? Value! : throw new InvalidOperationException(Reason);

    public override string ToString() => HasValue ? $"{Value}" : $"n/a ({Reason})";
}
