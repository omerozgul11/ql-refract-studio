using Avalonia.Controls;
using RefractionStudio.App.ViewModels;

namespace RefractionStudio.App.Views;

public partial class QcPageView : UserControl
{
    public QcPageView()
    {
        InitializeComponent();
        FindingList.DoubleTapped += (_, _) =>
        {
            if (DataContext is QcPageViewModel vm) vm.GoToCommand.Execute(vm.Selected);
        };
    }
}
