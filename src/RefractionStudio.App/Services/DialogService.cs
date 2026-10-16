using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using RefractionStudio.App.Views;

namespace RefractionStudio.App.Services;

/// <summary>Kind of a message dialog (also shown as an icon and label — never colour alone).</summary>
public enum MessageKind
{
    Information,
    Warning,
    Error,
    Question,
}

/// <summary>A file-type filter.</summary>
public sealed record FileFilter(string Name, params string[] Extensions);

/// <summary>A message or question for the user.</summary>
public sealed record DialogMessage(string Title, string Message, MessageKind Kind, string? Details, IReadOnlyList<string> Buttons);

/// <summary>File pickers, message dialogs and the clipboard, bound to the main window.</summary>
public sealed class DialogService
{
    public Window? Owner { get; set; }

    public static readonly FileFilter ProjectFilter = new("Refraction Studio project", "*.rfsx");
    public static readonly FileFilter SeismicFilter = new("Seismic records (SEG-2, SEG-Y)", "*.sg2", "*.seg2", "*.dat", "*.sgy", "*.segy");

    private IStorageProvider Storage => Owner?.StorageProvider ?? throw new InvalidOperationException("The main window is not available.");

    private static List<FilePickerFileType> Types(IEnumerable<FileFilter> filters)
        => [.. filters.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Extensions })];

    public async Task<IReadOnlyList<string>> OpenFilesAsync(string title, bool multiple, params FileFilter[] filters)
    {
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = multiple, FileTypeFilter = Types(filters) });
        return [.. files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!)];
    }

    public async Task<string?> OpenFolderAsync(string title)
    {
        var folders = await Storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, string defaultExtension, params FileFilter[] filters)
    {
        var file = await Storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = defaultExtension,
            FileTypeChoices = Types(filters),
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>Shows a message with optional technical details ("Show details").</summary>
    public Task ShowAsync(string title, string message, MessageKind kind = MessageKind.Information, string? details = null)
        => MessageHandler is { } h ? h(new DialogMessage(title, message, kind, details, ["OK"])) : MessageDialog.ShowAsync(Owner, title, message, kind, details, ["OK"]);

    /// <summary>Asks a question; returns the index of the chosen button (or −1 when closed).</summary>
    public Task<int> AskAsync(string title, string message, MessageKind kind, params string[] buttons)
        => MessageHandler is { } h ? h(new DialogMessage(title, message, kind, null, buttons)) : MessageDialog.ShowAsync(Owner, title, message, kind, null, buttons);

    /// <summary>
    /// When set, messages and questions are passed to this handler instead of a window; it returns the
    /// index of the chosen button. Used for automation (UI tests).
    /// </summary>
    public Func<DialogMessage, Task<int>>? MessageHandler { get; set; }

    public async Task SetClipboardTextAsync(string text)
    {
        if (Owner?.Clipboard is { } c) await c.SetTextAsync(text);
    }

    public async Task SetClipboardImageAsync(byte[] png)
    {
        if (Owner?.Clipboard is not { } c) return;
        using var ms = new MemoryStream(png);
        var bitmap = new Avalonia.Media.Imaging.Bitmap(ms);
        await c.SetBitmapAsync(bitmap);
    }
}
