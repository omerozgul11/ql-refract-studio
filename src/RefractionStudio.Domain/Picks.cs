namespace RefractionStudio.Domain;

/// <summary>How a pick came into being.</summary>
public enum ArrivalOrigin
{
    /// <summary>Placed or moved by the user.</summary>
    Manual,

    /// <summary>Produced by an automatic picker and not reviewed.</summary>
    Automatic,

    /// <summary>An automatic suggestion explicitly accepted by the user.</summary>
    AcceptedSuggestion,

    /// <summary>Transferred from a neighbouring/reciprocal trace by cross-correlation.</summary>
    Correlation,
}

/// <summary>Coarse confidence class shown to users.</summary>
public enum ConfidenceLevel
{
    High,
    Medium,
    Low,
}

/// <summary>One measure contributing to a confidence score.</summary>
/// <param name="Name">Short name, e.g. "Signal-to-noise".</param>
/// <param name="Score">Normalised 0–1 score of this factor.</param>
/// <param name="Weight">Relative weight in the combined score.</param>
/// <param name="Explanation">What was measured, in words, including the value.</param>
public sealed record ConfidenceFactor(string Name, double Score, double Weight, string Explanation);

/// <summary>Confidence of an automatic pick with an explanation of why.</summary>
/// <param name="Score">Combined 0–1 score.</param>
/// <param name="Level">Class derived from <paramref name="Score"/> with documented thresholds.</param>
/// <param name="Factors">Contributing measures.</param>
/// <param name="Reasons">Plain-language reasons for a reduced confidence.</param>
public sealed record PickConfidence(double Score, ConfidenceLevel Level, ImmutableArray<ConfidenceFactor> Factors, ImmutableArray<string> Reasons)
{
    /// <summary>Score at or above which confidence is High.</summary>
    public const double HighThreshold = 0.7;

    /// <summary>Score at or above which confidence is Medium.</summary>
    public const double MediumThreshold = 0.4;

    public static ConfidenceLevel LevelFor(double score)
        => score >= HighThreshold ? ConfidenceLevel.High : score >= MediumThreshold ? ConfidenceLevel.Medium : ConfidenceLevel.Low;
}

/// <summary>An automatic first-break estimate kept for reference.</summary>
public sealed record PickSuggestion(double Time, string Algorithm, PickConfidence Confidence, double? Snr);

/// <summary>A first-arrival pick on a gather trace.</summary>
/// <param name="Time">Pick time in record time (s): relative to the trigger, before shot-time correction.</param>
/// <param name="Origin">How it was made.</param>
/// <param name="Algorithm">Picker for automatic picks.</param>
/// <param name="Confidence">Confidence (automatic picks).</param>
/// <param name="Snr">Signal-to-noise ratio around the pick (amplitude ratio).</param>
/// <param name="Included">False to exclude from analysis (remains visible and auditable).</param>
/// <param name="Comment">User comment.</param>
/// <param name="Suggestion">The automatic suggestion that existed when the pick was last changed.</param>
public sealed record ArrivalPick(
    double Time,
    ArrivalOrigin Origin,
    string? Algorithm = null,
    PickConfidence? Confidence = null,
    double? Snr = null,
    bool Included = true,
    string? Comment = null,
    PickSuggestion? Suggestion = null)
{
    public bool IsManual => Origin is ArrivalOrigin.Manual or ArrivalOrigin.AcceptedSuggestion;
}
