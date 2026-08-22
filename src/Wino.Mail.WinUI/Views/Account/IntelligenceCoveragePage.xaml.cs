using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;
using Wino.Views.Abstract;

namespace Wino.Views;

public sealed partial class IntelligenceCoveragePage : IntelligenceCoveragePageAbstract
{
    public IntelligenceCoveragePage() => InitializeComponent();

    /// <summary>
    /// Invoking a row loads that folder's rule into the editor. Ticking its checkbox does not,
    /// because including a folder and looking at one are separate intentions.
    /// </summary>
    private void FolderTreeView_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is IntelligenceFolderNode node)
            ViewModel.SelectFolderCommand.Execute(node);
    }

    /// <summary>
    /// The bar width is a pixel value rather than a star column, because the bars live in an
    /// items panel with no shared measuring pass to divide the space for them.
    /// </summary>
    private void HistogramHost_SizeChanged(object sender, SizeChangedEventArgs e)
        => ViewModel.SetHistogramWidth(e.NewSize.Width);
}
