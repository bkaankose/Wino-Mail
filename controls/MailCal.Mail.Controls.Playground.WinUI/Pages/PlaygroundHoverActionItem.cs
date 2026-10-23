using System.ComponentModel;
using MailCal.Mail.Controls.Core;

namespace MailCal.Mail.Controls.Playground.Pages;

public sealed class PlaygroundHoverActionItem : IMailListSourceItem
{
    public event PropertyChangedEventHandler? PropertyChanged
    {
        add { }
        remove { }
    }

    public Guid StableId { get; } = Guid.NewGuid();

    public string? ThreadKey => null;

    public DateTimeOffset DateSortKey => DateTimeOffset.Now;

    public string NameSortKey => "Hover action sample";

    public bool IsPinned => false;
}
