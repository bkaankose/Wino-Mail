using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Reader;
using WinRT;

namespace Wino.Mail.WinUI.Dialogs;

/// <summary>One row of the checks dialog with its position, so a button can say which problem it acts on.</summary>
internal sealed partial class DraftCheckIssueRow(int index, DraftCheckIssueItem item)
{
    public int Index { get; } = index;

    public DraftCheckIssueItem Item { get; } = item;
}

/// <summary>
/// What the checks before sending found. Each row's button closes the dialog with that choice; the composer applies it
/// and shows the dialog again with what is left.
/// </summary>
public sealed partial class DraftCheckDialog : ContentDialog
{
    internal List<DraftCheckIssueRow> Rows { get; }

    public DraftCheckDialogResult Result { get; private set; } = new(DraftCheckDialogChoice.GoBack);

    public DraftCheckDialog(IReadOnlyList<DraftCheckIssueItem> issues)
    {
        Rows = issues.Select((issue, index) => new DraftCheckIssueRow(index, issue)).ToList();

        InitializeComponent();

        MessageText.Text = issues.Count == 1
            ? Translator.DraftCheckDialog_MessageOne
            : string.Format(CultureInfo.CurrentCulture, Translator.DraftCheckDialog_MessageFormat, issues.Count);
    }

    private void SendAnywayClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        => Result = new DraftCheckDialogResult(DraftCheckDialogChoice.SendAnyway);

    private void ShowClicked(object sender, RoutedEventArgs e) => Choose(sender, DraftCheckIssueAction.Show);

    private void FixClicked(object sender, RoutedEventArgs e) => Choose(sender, DraftCheckIssueAction.Fix);

    private void DescribeClicked(object sender, RoutedEventArgs e) => Choose(sender, DraftCheckIssueAction.Describe);

    private void NotNeededClicked(object sender, RoutedEventArgs e) => Choose(sender, DraftCheckIssueAction.NotNeeded);

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private void Choose(object sender, DraftCheckIssueAction action)
    {
        if ((sender as FrameworkElement)?.Tag is not DraftCheckIssueRow row)
            return;

        Result = new DraftCheckDialogResult(DraftCheckDialogChoice.IssueAction, row.Index, action);
        Hide();
    }
}
