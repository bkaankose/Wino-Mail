using System.ComponentModel;
using Wino.Mail.Controls.Core;

namespace Wino.Mail.Controls.Playground.Pages;

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
