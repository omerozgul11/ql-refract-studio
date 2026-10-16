using Avalonia.Controls;
using Avalonia.Controls.Templates;
using RefractionStudio.App.ViewModels;

namespace RefractionStudio.App;

/// <summary>Maps a view model "…ViewModel" to its view "…View" in the Views namespace.</summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? data)
    {
        if (data is null) return null;
        var name = data.GetType().FullName!.Replace(".ViewModels.", ".Views.", StringComparison.Ordinal).Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);
        return type is not null ? (Control)Activator.CreateInstance(type)! : new TextBlock { Text = "View not found: " + name };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
