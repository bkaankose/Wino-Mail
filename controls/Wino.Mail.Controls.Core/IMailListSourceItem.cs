using System.ComponentModel;

namespace Wino.Mail.Controls.Core;

/// <summary>
/// Supplies only the stable identity and ordering metadata required by a mail list.
/// Mail content and domain state belong to application-specific interfaces.
/// </summary>
public interface IMailListSourceItem : INotifyPropertyChanged
{
    Guid StableId { get; }

    string? ThreadKey { get; }

    DateTimeOffset DateSortKey { get; }

    string NameSortKey { get; }

    bool IsPinned { get; }
}
