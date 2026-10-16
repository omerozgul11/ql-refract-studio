using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using RefractionStudio.App.ViewModels;

namespace RefractionStudio.App.Views;

public partial class ImportPageView : UserControl
{
    public ImportPageView()
    {
        InitializeComponent();
        DropZone.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
            DropZone.BorderBrush = this.FindResource("AccentBrush") as Avalonia.Media.IBrush;
            e.Handled = true;
        });
        DropZone.AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropZone.BorderBrush = this.FindResource("BorderBrush") as Avalonia.Media.IBrush);
        DropZone.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            DropZone.BorderBrush = this.FindResource("BorderBrush") as Avalonia.Media.IBrush;
            e.Handled = true;
            var paths = (e.DataTransfer.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths.Count > 0 && DataContext is ImportPageViewModel vm) await vm.AddPathsAsync(paths);
        });
    }
}
