using Wino.Core.Domain.Entities.Mail;
using Wino.Mail.Controls.Core;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>The mails a row command acts on (Windows <c>MailListContextRequested</c>, macOS <c>BuildContextMenu</c>).</summary>
internal readonly record struct MailListTargets(MailItemViewModel[] Items, MailItemViewModel? ComposeTarget)
{
    public bool IsEmpty => Items.Length == 0;

    public MailCopy[] Mails => [.. Items.Select(static item => item.MailCopy)];

    /// <summary>A thread head acts on its leaves; a row that is selected acts on the whole selection; any other row acts on itself.</summary>
    public static MailListTargets For(MailListRow row, MailListPageViewModel viewModel)
    {
        if (row.IsThreadHead) return new([.. row.LeafItems.OfType<MailItemViewModel>()], row.SourceItem as MailItemViewModel);
        if (row.SourceItem is not MailItemViewModel item) return new([], null);
        return viewModel.IsMailSelected(item.UniqueId) ? new([.. viewModel.SelectedItems], item) : new([item], item);
    }
}
