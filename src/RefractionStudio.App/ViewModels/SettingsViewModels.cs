using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RefractionStudio.App.Services;
using RefractionStudio.Domain;
using RefractionStudio.Domain.Units;
using RefractionStudio.Infrastructure;
using RefractionStudio.Licensing;

namespace RefractionStudio.App.ViewModels;

/// <summary>Application settings (sections: General, Appearance, Units, Processing and picking defaults, Reporting, License, Updates, Advanced).</summary>
public sealed partial class SettingsViewModel(StudioContext ctx, UpdateService updates) : ViewModelBase
{
    public IReadOnlyList<string> Sections { get; } = ["General", "Appearance", "Units", "Processing and picking", "Reporting", "License", "Updates", "Advanced"];
    public IReadOnlyList<ThemePreference> Themes { get; } = Enum.GetValues<ThemePreference>();
    public IReadOnlyList<UnitSystem> UnitSystems { get; } = Enum.GetValues<UnitSystem>();
    public IReadOnlyList<ModulusDisplayUnit> ModulusUnits { get; } = Enum.GetValues<ModulusDisplayUnit>();
    public IReadOnlyList<UpdateMode> UpdateModes { get; } = Enum.GetValues<UpdateMode>();
    public IReadOnlyList<string> Channels { get; } = ["stable", "beta"];
    public IReadOnlyList<string> Papers { get; } = ["A4", "Letter"];
    public IReadOnlyList<string> Colormaps { get; } = [.. Visualization.Colormap.All.Select(c => c.Name)];
    public IReadOnlyList<PickerAlgorithm> Pickers { get; } = [PickerAlgorithm.Aic, PickerAlgorithm.StaLta, PickerAlgorithm.EnergyRatio];

    [ObservableProperty] public partial string Section { get; set; } = "General";
    [ObservableProperty] public partial string UserName { get; set; } = "";
    [ObservableProperty] public partial bool Expert { get; set; }
    [ObservableProperty] public partial decimal AutosaveMinutes { get; set; } = 3;
    [ObservableProperty] public partial ThemePreference Theme { get; set; }
    [ObservableProperty] public partial string Colormap { get; set; } = "Viridis";
    [ObservableProperty] public partial UnitSystem Units { get; set; }
    [ObservableProperty] public partial ModulusDisplayUnit ModulusUnit { get; set; }
    [ObservableProperty] public partial PickerAlgorithm DefaultPicker { get; set; }
    [ObservableProperty] public partial bool DefaultCausalHighPass { get; set; }
    [ObservableProperty] public partial decimal DefaultHighPassHz { get; set; } = 10;
    [ObservableProperty] public partial string Paper { get; set; } = "A4";
    [ObservableProperty] public partial string CompanyName { get; set; } = "";
    [ObservableProperty] public partial string? Address { get; set; }
    [ObservableProperty] public partial string? Phone { get; set; }
    [ObservableProperty] public partial string? Email { get; set; }
    [ObservableProperty] public partial string? Website { get; set; }
    [ObservableProperty] public partial string? Footer { get; set; }
    [ObservableProperty] public partial string? EngineerName { get; set; }
    [ObservableProperty] public partial string? Registration { get; set; }
    [ObservableProperty] public partial string? LogoBase64 { get; set; }
    [ObservableProperty] public partial bool SendMachineName { get; set; }
    [ObservableProperty] public partial UpdateMode UpdateMode { get; set; }
    [ObservableProperty] public partial string Channel { get; set; } = "stable";
    [ObservableProperty] public partial bool VerboseLogging { get; set; }
    [ObservableProperty] public partial string LicenseServer { get; set; } = "";
    [ObservableProperty] public partial string Message { get; set; } = "";

    public string DataFolder => ctx.Paths.Root;
    public string PrivacyUrl => AppInfo.PrivacyPolicyUrl;
    public bool HasLogo => !string.IsNullOrEmpty(LogoBase64);

    public void Load()
    {
        var s = ctx.Settings.Current;
        UserName = s.UserName ?? Environment.UserName;
        Expert = s.Mode == UserMode.Expert;
        AutosaveMinutes = s.AutosaveMinutes;
        Theme = s.Theme;
        Colormap = s.Colormap;
        Units = s.Units;
        ModulusUnit = s.ModulusUnit;
        DefaultPicker = s.DefaultPicker.Algorithm == PickerAlgorithm.ShPolarityDivergence ? PickerAlgorithm.Aic : s.DefaultPicker.Algorithm;
        var hp = s.DefaultPipeline.EnabledSteps.OfType<HighPassStep>().FirstOrDefault();
        DefaultCausalHighPass = hp is not null;
        DefaultHighPassHz = (decimal)(hp?.CornerHz ?? 10);
        Paper = s.PaperSize;
        CompanyName = s.Branding.CompanyName;
        Address = s.Branding.Address;
        Phone = s.Branding.Phone;
        Email = s.Branding.Email;
        Website = s.Branding.Website;
        Footer = s.Branding.Footer;
        EngineerName = s.Branding.EngineerName;
        Registration = s.Branding.Registration;
        LogoBase64 = s.Branding.LogoPngBase64;
        SendMachineName = s.SendMachineName;
        UpdateMode = s.Updates;
        Channel = s.UpdateChannel;
        VerboseLogging = s.VerboseLogging;
        LicenseServer = s.LicenseServer;
        Message = "";
        OnPropertyChanged(nameof(HasLogo));
    }

    [RelayCommand]
    private void Apply()
    {
        if (!Uri.TryCreate(LicenseServer, UriKind.Absolute, out var server) || server.Scheme != Uri.UriSchemeHttps)
        {
            Message = "The licence server address must be an https:// URL.";
            return;
        }
        var pipeline = DefaultCausalHighPass
            ? new ProcessingPipeline([new RemoveMeanStep(StepId.New(), true), new HighPassStep(StepId.New(), true, (double)DefaultHighPassHz, 2, false)])
            : ProcessingPipeline.Recommended;
        ctx.Settings.Update(s => s with
        {
            UserName = string.IsNullOrWhiteSpace(UserName) ? null : UserName.Trim(),
            Mode = Expert ? UserMode.Expert : UserMode.Guided,
            AutosaveMinutes = (int)Math.Clamp(AutosaveMinutes, 1, 60),
            Theme = Theme,
            Colormap = Colormap,
            Units = Units,
            ModulusUnit = ModulusUnit,
            DefaultPipeline = pipeline,
            DefaultPicker = s.DefaultPicker with { Algorithm = DefaultPicker },
            PaperSize = Paper,
            Branding = new CompanyBranding(CompanyName, Address, Phone, Email, Website, Footer, EngineerName, Registration, LogoBase64),
            SendMachineName = SendMachineName,
            Updates = UpdateMode,
            UpdateChannel = Channel,
            VerboseLogging = VerboseLogging,
            LicenseServer = LicenseServer.EndsWith('/') ? LicenseServer : LicenseServer + "/",
        });
        Message = "Settings saved. Changes to the licence server, logging or machine-name sharing apply after a restart.";
    }

    [RelayCommand]
    private async Task ChooseLogoAsync()
    {
        var files = await ctx.Dialogs.OpenFilesAsync("Company logo (PNG or JPEG)", false, new FileFilter("Images", "*.png", "*.jpg", "*.jpeg"));
        if (files.Count == 0) return;
        var bytes = await File.ReadAllBytesAsync(files[0]);
        if (bytes.Length > 2_000_000)
        {
            Message = "The logo is larger than 2 MB; choose a smaller image.";
            return;
        }
        using var bitmap = SkiaSharp.SKBitmap.Decode(bytes);
        if (bitmap is null)
        {
            Message = "The file is not a readable PNG or JPEG image.";
            return;
        }
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        LogoBase64 = Convert.ToBase64String(png.ToArray());
        OnPropertyChanged(nameof(HasLogo));
    }

    [RelayCommand]
    private void RemoveLogo()
    {
        LogoBase64 = null;
        OnPropertyChanged(nameof(HasLogo));
    }

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        var path = await ctx.Dialogs.SaveFileAsync("Export diagnostic package", $"RefractionStudio-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip", "zip", new FileFilter("Zip archive", "*.zip"));
        if (path is null) return;
        var include = await ctx.Dialogs.AskAsync("Include project files?",
            "The package contains logs, version, operating system and basic hardware information. Project and survey files are NOT included unless you choose them now.",
            MessageKind.Question, "Logs only", "Also choose files…");
        IReadOnlyList<string> files = [];
        if (include == 1) files = await ctx.Dialogs.OpenFilesAsync("Files to include", true, new FileFilter("All files", "*"));
        DiagnosticPackage.Create(path, ctx.Paths, ctx.Settings.Current, files);
        Message = $"Diagnostic package written to {path}. Nothing was sent automatically.";
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        var r = await updates.CheckAsync(Channel, AppInfo.Version);
        Message = r.Message;
    }
}

/// <summary>Help: context topics and the user guide.</summary>
public sealed partial class HelpViewModel : ViewModelBase
{
    public ObservableCollection<HelpTopic> Topics { get; } = [];

    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial HelpTopic? Selected { get; set; }
    [ObservableProperty] public partial bool ShowGuide { get; set; }

    public string UserGuide { get; } = HelpCatalog.UserGuide();

    public HelpViewModel() => Search();

    partial void OnQueryChanged(string value) => Search();

    private void Search()
    {
        Topics.Clear();
        foreach (var t in HelpCatalog.Search(Query)) Topics.Add(t);
    }

    public void ShowTopic(string id)
    {
        Query = "";
        Selected = HelpCatalog.Get(id) ?? HelpCatalog.Get("workflow");
        ShowGuide = false;
    }

    [RelayCommand]
    private void OpenGuide() => ShowGuide = true;
}

/// <summary>Licence screens: welcome (trial / activate), status, deactivate, refresh.</summary>
public sealed partial class LicenseViewModel : ViewModelBase
{
    private readonly StudioContext ctx;

    public LicenseViewModel(StudioContext context)
    {
        ctx = context;
        ctx.License.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LicenseService.Status)) Refresh();
        };
    }

    [ObservableProperty] public partial string Email { get; set; } = "";
    [ObservableProperty] public partial string LicenseKey { get; set; } = "";
    [ObservableProperty] public partial string Message { get; set; } = "";
    [ObservableProperty] public partial bool IsWorking { get; set; }

    public LicenseStatus Status => ctx.License.Status;
    public bool IsActivated => Status.State != LicenseState.NotActivated;
    public string DeviceName => Status.Claims is not null ? (ctx.Settings.Current.SendMachineName ? Environment.MachineName : "This computer") : "";
    public string KeyHint => Status.Claims?.KeyHint is { } h ? $"{AppInfo.LicenseKeyPrefix}-XXXX-XXXX-XXXX-{h}" : "—";
    public string LastVerified => Status.LastVerifiedText(DateTimeOffset.UtcNow) ?? "Not yet verified online";
    public string Edition => Status.Claims is { } c ? $"{AppInfo.ProductName} {c.Edition}{(c.Kind == LicenseKind.Trial ? " (trial)" : "")}" : AppInfo.ProductName;
    public string LicensedTo => Status.Claims?.LicensedTo ?? (Status.Claims?.Kind == LicenseKind.Trial ? "Trial" : "—");
    public string Expiry => Status.Claims switch
    {
        { Kind: LicenseKind.Trial, TrialEnd: { } e } => $"Trial ends {e.ToLocalTime():d MMMM yyyy}",
        { ExpiresAt: { } e } => $"Valid until {e.ToLocalTime():d MMMM yyyy}",
        { } => "Perpetual",
        _ => "—",
    };
    public string Maintenance => Status.Claims?.MaintenanceUntil is { } m ? $"Updates included until {m.ToLocalTime():d MMMM yyyy}" : "";

    private void Refresh()
    {
        foreach (var name in new[] { nameof(Status), nameof(IsActivated), nameof(DeviceName), nameof(KeyHint), nameof(LastVerified), nameof(Edition), nameof(LicensedTo), nameof(Expiry), nameof(Maintenance) })
            OnPropertyChanged(name);
    }

    private async Task RunAsync(Func<Task<LicenseStatus>> action)
    {
        IsWorking = true;
        Message = "Contacting the licence server…";
        try
        {
            var status = await action();
            Message = status.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Message = "The licence server could not be reached. Check the internet connection and try again.";
        }
        finally
        {
            IsWorking = false;
            ctx.License.Reevaluate();
            Refresh();
        }
    }

    [RelayCommand]
    private Task StartTrialAsync() => RunAsync(() => ctx.License.Manager.StartTrialAsync(Email));

    [RelayCommand]
    private Task ActivateAsync() => RunAsync(() => ctx.License.Manager.ActivateAsync(LicenseKey, string.IsNullOrWhiteSpace(Email) ? null : Email.Trim(), AppInfo.LicenseKeyPrefix));

    [RelayCommand]
    private Task RefreshLicenseAsync() => RunAsync(() => ctx.License.Manager.RefreshAsync());

    [RelayCommand]
    private async Task DeactivateAsync()
    {
        if (await ctx.Dialogs.AskAsync("Deactivate this computer",
                "The licence will be released from this computer so that it can be activated on another one. Your projects are not affected.",
                MessageKind.Question, "Deactivate", "Cancel") != 0) return;
        IsWorking = true;
        try
        {
            var (_, message) = await ctx.License.Manager.DeactivateAsync();
            Message = message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Message = "The licence server could not be reached. Check the internet connection and try again.";
        }
        finally
        {
            IsWorking = false;
            ctx.License.Reevaluate();
            Refresh();
        }
    }

    [RelayCommand]
    private async Task CopyInfoAsync()
    {
        var s = Status;
        var text = $"{Edition}\nStatus: {s.Title}\nLicensed to: {LicensedTo}\nLicence key: {KeyHint}\n{Expiry}\n{LastVerified}\nApplication: {AppInfo.Version}\nDevice: {ctx.License.Manager.Device.Fingerprint[..12]}…";
        await ctx.Dialogs.SetClipboardTextAsync(text);
        Message = "Licence information copied to the clipboard.";
    }
}
