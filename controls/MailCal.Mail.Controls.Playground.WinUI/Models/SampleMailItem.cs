using System.ComponentModel;
using MailCal.Mail.Controls.Core;

namespace MailCal.Mail.Controls.Playground.Models;

public sealed class SampleMailItem(
    string name,
    string? threadKey,
    DateTimeOffset dateSortKey,
    bool isPinned = false) : IMailListSourceItem
{
    public Guid StableId { get; } = Guid.NewGuid();

    public string? ThreadKey { get; } = threadKey;

    public DateTimeOffset DateSortKey { get; } = dateSortKey;

    public string NameSortKey { get; } = name;

    public bool IsPinned { get; } = isPinned;

    public event PropertyChangedEventHandler? PropertyChanged
    {
        add { }
        remove { }
    }
}
