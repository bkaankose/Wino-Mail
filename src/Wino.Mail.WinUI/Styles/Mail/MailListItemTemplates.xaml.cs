using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wino.Mail.Controls.Core;
using Wino.Views.Mail;

namespace Wino.Mail.WinUI.Styles.Mail;

/// <summary>
/// Mail row body templates shared by the mail list and the Settings message list preview.
/// Connects the subject expander to its containing mail row.
/// </summary>
public sealed partial class MailListItemTemplates
{
    public MailListItemTemplates()
    {
        InitializeComponent();
    }

    private void ThreadChevronLoaded(object sender, RoutedEventArgs e)
    {
        var presenter = (ContentPresenter)sender;
        presenter.Content = null;
        presenter.Visibility = Visibility.Collapsed;

        // The shared body also appears in Settings, where there is no mail row.
        for (var parent = VisualTreeHelper.GetParent(presenter); parent != null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is FrameworkElement { DataContext: MailListRow row })
            {
                if (row.Kind == MailListRowKind.ThreadHead)
                {
                    presenter.Content = row;
                    presenter.Visibility = Visibility.Visible;
                }

                return;
            }
        }
    }

    private void ThreadChevronUnloaded(object sender, RoutedEventArgs e)
    {
        var presenter = (ContentPresenter)sender;
        presenter.Content = null;
        presenter.Visibility = Visibility.Collapsed;
    }

    private void ThreadChevronPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        for (var parent = VisualTreeHelper.GetParent((DependencyObject)sender); parent != null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is MailListPage page)
            {
                page.ThreadExpanderPointerPressed(sender, e);
                return;
            }
        }
    }
}
