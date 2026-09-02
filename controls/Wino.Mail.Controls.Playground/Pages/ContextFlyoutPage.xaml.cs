using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wino.Mail.Controls.ContextFlyout;

namespace Wino.Mail.Controls.Playground.Pages;

public sealed partial class ContextFlyoutPage : Page
{
    public ContextFlyoutPage()
    {
        SampleCommand = new RelayCommand<object?>(parameter =>
        {
            StatusTextBlock.Text = $"Invoked: {parameter}";
        });
        DisabledCommand = new RelayCommand(() => { }, () => false);
        BoundItems = CreateBoundItems();

        InitializeComponent();
    }

    public RelayCommand<object?> SampleCommand { get; }

    public RelayCommand DisabledCommand { get; }

    public ObservableCollection<DependencyObject> BoundItems { get; }

    private ObservableCollection<DependencyObject> CreateBoundItems()
    {
        var items = new ObservableCollection<DependencyObject>();

        for (var index = 1; index <= 24; index++)
        {
            items.Add(new WinoContextFlyoutItem
            {
                Text = $"Move to › Projects › Folder {index:00}",
                Breadcrumb = "Move to › Projects",
                SearchKeywords = "folder destination",
                Command = SampleCommand,
                CommandParameter = $"Folder {index:00}",
                IconSource = new SymbolIconSource { Symbol = Symbol.Folder }
            });
        }

        return items;
    }
}
