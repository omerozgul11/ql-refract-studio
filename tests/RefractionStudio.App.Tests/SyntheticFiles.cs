using RefractionStudio.Synthetics;

namespace RefractionStudio.App.Tests;

/// <summary>Writes small synthetic SEG-2 shot files for import tests.</summary>
public static class SyntheticFiles
{
    public static IReadOnlyList<string> Write(string folder, int shots)
    {
        var positions = Enumerable.Range(0, shots).Select(i => -2.0 + i * 16.0).ToArray();
        var survey = SurveyGenerator.Generate(new HorizontalLayersModel([500, 2000], [5]),
            new SyntheticSurveyOptions(Channels: 24, Spacing: 2, ShotPositions: positions, Samples: 512, NoiseLevel: 0.02));
        return survey.WriteTo(folder);
    }
}
