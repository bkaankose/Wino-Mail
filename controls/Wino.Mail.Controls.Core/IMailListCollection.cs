using System.Collections.Specialized;

namespace Wino.Mail.Controls.Core;

public interface IMailListCollection :
    INotifyCollectionChanged,
    IEnumerable<IMailListSourceItem>
{
    int Count { get; }

    IReadOnlyCollection<Guid> ItemIds { get; }

    bool IsBatchUpdating { get; }

    event EventHandler? BatchUpdateCompleted;

    bool ContainsId(Guid id);

    bool TryGetItem(Guid id, out IMailListSourceItem? item);
}
