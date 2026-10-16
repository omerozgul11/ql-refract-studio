using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RefractionStudio.App.Controls;
using RefractionStudio.App.Services;
using RefractionStudio.Core.Services;
using RefractionStudio.Core.Session;
using RefractionStudio.Domain;
using RefractionStudio.Persistence;
using RefractionStudio.Reporting;

namespace RefractionStudio.App.ViewModels;

/// <summary>Editable channel row.</summary>
public sealed partial class ChannelRow(ChannelAssignment a, double? position) : ViewModelBase
{
    public int Channel { get; } = a.Channel;
    public double? Position { get; } = position;
    public string PositionText => Position is { } p ? p.ToString("0.###", CultureInfo.InvariantCulture) : "at shot";
    [ObservableProperty] public partial ChannelRole Role { get; set; } = a.Role;
    [ObservableProperty] public partial string XOverride { get; set; } = a.XOverride?.ToString("R", CultureInfo.InvariantCulture) ?? "";
    [ObservableProperty] public partial string ElevationOverride { get; set; } = a.ElevationOverride?.ToString("R", CultureInfo.InvariantCulture) ?? "";
    [ObservableProperty] public partial bool InvertPolarity { get; set; } = a.InvertPolarity;
    public ChannelAssignment Original { get; } = a;
}

/// <summary>Editable shot row.</summary>
public sealed partial class ShotRow(ShotGather g, SurveyLine line) : ViewModelBase
{
    public ShotGather Gather { get; } = g;
    public string Label => Gather.Label;
    [ObservableProperty] public partial string X { get; set; } = double.IsFinite(g.ShotX) ? g.ShotX.ToString("R", CultureInfo.InvariantCulture) : "";
    [ObservableProperty] public partial string Elevation { get; set; } = g.ShotElevation?.ToString("R", CultureInfo.InvariantCulture) ?? "";
    [ObservableProperty] public partial string Depth { get; set; } = g.ShotDepth.ToString("R", CultureInfo.InvariantCulture);
    [ObservableProperty] public partial bool Included { get; set; } = g.Included;
    public string GroundElevation => double.IsFinite(Gather.ShotX) ? line.ShotGroundElevation(Gather).ToString("0.##", CultureInfo.InvariantCulture) : "—";
    public string Records => Gather.Combine == CombineMode.Single
        ? "1 record"
        : $"{Gather.Members.Length} records ({string.Join(" / ", Gather.Members.Select(m => $"{m.Kind switch { SourceKind.ShLeft => "L", SourceKind.ShRight => "R", _ => "·" }}{(m.Polarity > 0 ? "+" : "−")}"))})";
    public bool NeedsPosition => !double.IsFinite(Gather.ShotX);
    public string? Comment => Gather.Comment;
}

/// <summary>Line geometry: spreads, channel roles, shots, topography.</summary>
public sealed partial class GeometryPageViewModel(StudioContext ctx) : PageViewModel(ctx)
{
    public override string Title => "Geometry";
    public override string HelpTopic => "geometry";
    public override WorkflowStep Step => WorkflowStep.Geometry;
    public override string GuidedText => "Check the receiver layout, channel roles and shot positions. Positions recorded in the files were used automatically. Enter the surveyed ground profile so that elevations and off-end shots are correct; every result updates when geometry changes.";

    public ObservableCollection<SurveyLine> Lines { get; } = [];
    public ObservableCollection<ChannelRow> Channels { get; } = [];
    public ObservableCollection<ShotRow> Shots { get; } = [];
    public ObservableCollection<SpreadProfile> Profiles { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public IReadOnlyList<ChannelRole> Roles { get; } = Enum.GetValues<ChannelRole>();
    public IReadOnlyList<ChannelDirection> Directions { get; } = Enum.GetValues<ChannelDirection>();

    [ObservableProperty] public partial SurveyLine? SelectedLine { get; set; }
    [ObservableProperty] public partial Spread? SelectedSpread { get; set; }
    [ObservableProperty] public partial string LineName { get; set; } = "";
    [ObservableProperty] public partial decimal FirstReceiverX { get; set; }
    [ObservableProperty] public partial decimal Spacing { get; set; } = 2;
    [ObservableProperty] public partial ChannelDirection Direction { get; set; }
    [ObservableProperty] public partial string TopographyText { get; set; } = "";
    [ObservableProperty] public partial IPlotAdapter? Diagram { get; private set; }
    [ObservableProperty] public partial SpreadProfile? SelectedProfile { get; set; }
    [ObservableProperty] public partial string ProfileName { get; set; } = "";
    [ObservableProperty] public partial bool HasLines { get; private set; }

    public ObservableCollection<Spread> Spreads { get; } = [];

    private bool _loading;

    public override void Activate() => Reload();

    public override void OnProjectChanged() => Reload();

    public override void OnStateChanged(StateChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Previous.Survey, e.Current.Survey)) Reload();
    }

    private void Reload()
    {
        _loading = true;
        var state = Context.State;
        var selected = SelectedLine?.Id ?? Context.Selection.Line;
        Lines.Clear();
        foreach (var l in state?.Survey.Lines ?? []) Lines.Add(l);
        HasLines = Lines.Count > 0;
        SelectedLine = Lines.FirstOrDefault(l => l.Id == selected) ?? Lines.FirstOrDefault();
        _loading = false;
        LoadLine();
        _ = LoadProfilesAsync();
    }

    private async Task LoadProfilesAsync()
    {
        Profiles.Clear();
        foreach (var p in await Context.Catalog.SpreadProfilesAsync()) Profiles.Add(p);
    }

    partial void OnSelectedLineChanged(SurveyLine? value)
    {
        if (_loading || value is null) return;
        Context.Selection.Line = value.Id;
        LoadLine();
    }

    partial void OnSelectedSpreadChanged(Spread? value)
    {
        if (_loading || value is null) return;
        LoadSpread(value);
    }

    private void LoadLine()
    {
        Channels.Clear();
        Shots.Clear();
        Spreads.Clear();
        Warnings.Clear();
        var line = SelectedLine;
        if (line is null)
        {
            Diagram = null;
            return;
        }
        _loading = true;
        LineName = line.Name;
        foreach (var s in line.Spreads) Spreads.Add(s);
        var spreadId = SelectedSpread?.Id;
        SelectedSpread = Spreads.FirstOrDefault(s => s.Id == spreadId) ?? Spreads.FirstOrDefault();
        _loading = false;
        if (SelectedSpread is { } sp) LoadSpread(sp);
        foreach (var g in line.Gathers) Shots.Add(new ShotRow(g, line));
        TopographyText = string.Join("\n", line.Topography.Points.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.X:0.###}, {p.Elevation:0.###}")));
        Diagram = new SpreadPlot(PlotModels.Spread(line, Context.Selection.Gather));
        // Geometry checks.
        foreach (var g in line.Gathers.Where(g => !double.IsFinite(g.ShotX))) Warnings.Add($"Shot {g.Label} has no position; it is excluded until one is entered.");
        if (!line.Topography.IsFlat)
        {
            var positions = line.Gathers.Where(g => double.IsFinite(g.ShotX)).Select(g => g.ShotX)
                .Concat(line.Spreads.SelectMany(s => s.ReceiverChannels.Select(c => s.PositionOf(c.Channel))).Where(x => x is not null).Select(x => x!.Value));
            var outside = positions.Where(x => !line.Topography.Covers(x)).Distinct().Order().ToList();
            if (outside.Count > 0) Warnings.Add($"The ground profile does not cover x = {string.Join(", ", outside.Select(x => x.ToString("0.##", CultureInfo.InvariantCulture)))} m; the nearest end elevation is used there.");
        }
        else Warnings.Add("No ground profile entered: flat ground at elevation 0 is assumed.");
        if (line.Gathers.Count(g => g.Included) < 2) Warnings.Add("Fewer than two shots: velocities and depths cannot be determined reliably.");
    }

    private void LoadSpread(Spread sp)
    {
        _loading = true;
        FirstReceiverX = (decimal)sp.FirstReceiverX;
        Spacing = (decimal)sp.Spacing;
        Direction = sp.Direction;
        _loading = false;
        Channels.Clear();
        foreach (var c in sp.Channels) Channels.Add(new ChannelRow(c, sp.PositionOf(c.Channel)));
    }

    [RelayCommand]
    private void RenameLine()
    {
        if (SelectedLine is { } l && !string.IsNullOrWhiteSpace(LineName) && LineName != l.Name) Context.Edit(Edits.RenameLine(l.Id, LineName.Trim()));
    }

    [RelayCommand]
    private void ApplySpread()
    {
        if (SelectedLine is not { } l || SelectedSpread is not { } sp) return;
        if (Spacing <= 0)
        {
            _ = Context.Dialogs.ShowAsync("Invalid spacing", "The geophone spacing must be positive.", MessageKind.Warning);
            return;
        }
        Context.Edit(Edits.SetSpread(l.Id, sp with { FirstReceiverX = (double)FirstReceiverX, Spacing = (double)Spacing, Direction = Direction }));
    }

    [RelayCommand]
    private void ApplyChannel(ChannelRow? row)
    {
        if (row is null || SelectedLine is not { } l || SelectedSpread is not { } sp) return;
        if (!TryParseOptional(row.XOverride, out var x) || !TryParseOptional(row.ElevationOverride, out var z))
        {
            _ = Context.Dialogs.ShowAsync("Invalid number", $"Channel {row.Channel}: enter a number or leave the field empty.", MessageKind.Warning);
            return;
        }
        Context.Edit(Edits.SetChannelAssignment(l.Id, sp.Id, new ChannelAssignment(row.Channel, row.Role, x, z, row.InvertPolarity)));
    }

    [RelayCommand]
    private void ApplyShot(ShotRow? row)
    {
        if (row is null || SelectedLine is not { } l) return;
        if (!double.TryParse(row.X, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) || !double.IsFinite(x))
        {
            _ = Context.Dialogs.ShowAsync("Shot position required", $"Enter the position of shot {row.Label} in metres along the line.", MessageKind.Warning);
            return;
        }
        if (!TryParseOptional(row.Elevation, out var z) || !double.TryParse(row.Depth, NumberStyles.Float, CultureInfo.InvariantCulture, out var depth) || depth < 0)
        {
            _ = Context.Dialogs.ShowAsync("Invalid value", "Elevation must be a number or empty (= ground profile); depth must be zero or positive.", MessageKind.Warning);
            return;
        }
        using (Context.BeginGesture($"Edit shot {row.Label}"))
        {
            Context.Edit(Edits.SetGatherGeometry(l.Id, row.Gather.Id, x, z, depth));
            if (row.Included != row.Gather.Included) Context.Edit(Edits.SetGatherIncluded(l.Id, row.Gather.Id, row.Included, row.Label));
        }
    }

    [RelayCommand]
    private void FlipMemberPolarity(ShotRow? row)
    {
        if (row is null || SelectedLine is not { } l || row.Gather.Members.Length < 2) return;
        var members = row.Gather.Members.Select((m, i) => i == row.Gather.Members.Length - 1 ? m with { Polarity = -m.Polarity } : m).ToImmutableArray();
        Context.Edit(Edits.SetGatherMembers(l.Id, row.Gather.Id, row.Gather.Combine, members));
    }

    [RelayCommand]
    private void ToggleCombine(ShotRow? row)
    {
        if (row is null || SelectedLine is not { } l || row.Gather.Members.Length < 2) return;
        Context.Edit(Edits.SetGatherMembers(l.Id, row.Gather.Id, row.Gather.Combine == CombineMode.Stack ? CombineMode.Single : CombineMode.Stack, row.Gather.Members));
    }

    [RelayCommand]
    private void ApplyTopography()
    {
        if (SelectedLine is not { } l) return;
        var points = new List<TopographyPoint>();
        var lineNo = 0;
        foreach (var raw in TopographyText.Split('\n'))
        {
            lineNo++;
            var text = raw.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;
            var parts = text.Split([',', ';', '\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            {
                if (lineNo == 1 && points.Count == 0) continue; // header row
                _ = Context.Dialogs.ShowAsync("Invalid ground profile", $"Line {lineNo} (“{text}”) is not “x, elevation”.", MessageKind.Warning);
                return;
            }
            points.Add(new TopographyPoint(x, z));
        }
        try
        {
            var topo = points.Count == 0 ? Topography.Flat : Topography.Create(points);
            Context.Edit(Edits.SetTopography(l.Id, topo));
        }
        catch (ArgumentException ex)
        {
            _ = Context.Dialogs.ShowAsync("Invalid ground profile", ex.Message, MessageKind.Warning);
        }
    }

    [RelayCommand]
    private async Task ImportTopographyAsync()
    {
        var files = await Context.Dialogs.OpenFilesAsync("Ground profile (CSV: x, elevation)", false, new FileFilter("Text / CSV", "*.csv", "*.txt"));
        if (files.Count == 0) return;
        TopographyText = await File.ReadAllTextAsync(files[0]);
        ApplyTopography();
    }

    [RelayCommand]
    private async Task ApplyProfileAsync()
    {
        if (SelectedProfile is not { } p || SelectedLine is not { } l || SelectedSpread is not { } sp) return;
        if (await Context.Dialogs.AskAsync("Apply spread profile", $"Apply the channel roles of “{p.Name}” to this spread? Positions from the file headers are kept.", MessageKind.Question, "Apply", "Cancel") != 0) return;
        var channels = sp.Channels.Select(c => p.Channels.FirstOrDefault(pc => pc.Channel == c.Channel) is { } pc ? c with { Role = pc.Role, InvertPolarity = pc.InvertPolarity } : c).ToImmutableArray();
        Context.Edit(Edits.SetSpread(l.Id, sp with { Channels = channels }));
    }

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        if (SelectedSpread is not { } sp) return;
        var name = string.IsNullOrWhiteSpace(ProfileName) ? $"{sp.Channels.Length}-channel spread" : ProfileName.Trim();
        await Context.Catalog.SaveSpreadProfileAsync(new SpreadProfile(Guid.NewGuid(), name, null, sp.Channels.Length, sp.Spacing, sp.Direction, sp.Channels));
        ProfileName = "";
        await LoadProfilesAsync();
    }

    private static bool TryParseOptional(string text, out double? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v)) return false;
        value = v;
        return true;
    }
}
