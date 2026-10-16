using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RefractionStudio.App.ViewModels;
using RefractionStudio.Persistence;

namespace RefractionStudio.App.Views;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropHint.IsVisible = false);
        AddHandler(DragDrop.DropEvent, OnDrop);
        PaletteBox.AddHandler(KeyDownEvent, OnPaletteKeyDown, RoutingStrategies.Tunnel);
        PaletteList.DoubleTapped += async (_, _) => await RunSelectedPaletteCommandAsync();
        PaletteLayer.PointerPressed += (_, e) =>
        {
            if (e.Source == PaletteLayer && ViewModel is { } vm) vm.IsPaletteOpen = false;
        };
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel is { } vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.IsPaletteOpen) && vm.IsPaletteOpen)
                {
                    Dispatcher.UIThread.Post(() => PaletteBox.Focus(), DispatcherPriority.Input);
                }
            };
        }
    }

    private async void OnPaletteKeyDown(object? sender, KeyEventArgs e)
    {
        var count = PaletteList.ItemCount;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                PaletteList.SelectedIndex = Math.Min(count - 1, PaletteList.SelectedIndex + 1);
                PaletteList.ScrollIntoView(PaletteList.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Up when count > 0:
                PaletteList.SelectedIndex = Math.Max(0, PaletteList.SelectedIndex - 1);
                PaletteList.ScrollIntoView(PaletteList.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = true;
                await RunSelectedPaletteCommandAsync();
                break;
        }
    }

    private async Task RunSelectedPaletteCommandAsync()
    {
        if (ViewModel is { } vm) await vm.RunPaletteCommandCommand.ExecuteAsync(PaletteList.SelectedItem as PaletteCommand);
    }

    // ───────────────────────────── Drag and drop ─────────────────────────────

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        e.DragEffects = files is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
        DropHint.IsVisible = e.DragEffects != DragDropEffects.None && CurrentImportPageAcceptsDrop() is false;
    }

    /// <summary>The import page handles drops itself; the window-wide hint is shown elsewhere.</summary>
    private bool CurrentImportPageAcceptsDrop() => ViewModel?.CurrentPage is ImportPageViewModel;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropHint.IsVisible = false;
        if (e.Handled || ViewModel is not { } vm) return;
        var paths = (e.DataTransfer.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count == 0) return;
        e.Handled = true;
        var project = paths.FirstOrDefault(p => p.EndsWith(ProjectContainer.Extension, StringComparison.OrdinalIgnoreCase));
        if (project is not null)
        {
            await vm.OpenPathAsync(project);
            return;
        }
        if (!vm.HasProject) await vm.NewProjectCommand.ExecuteAsync(null);
        if (!vm.HasProject) return;
        vm.SelectedNav = vm.NavItems.FirstOrDefault(n => n.Page is ImportPageViewModel);
        await vm.Import.AddPathsAsync(paths);
    }

    // ───────────────────────────── Closing ─────────────────────────────

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || ViewModel is not { } vm) return;
        e.Cancel = true;
        if (!await vm.ConfirmCloseProjectAsync()) return;
        vm.Shutdown();
        _closeConfirmed = true;
        Close();
    }
}
