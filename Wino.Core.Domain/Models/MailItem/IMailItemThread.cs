using System.Collections.ObjectModel;

namespace Wino.Core.Domain.Models.MailItem
{
    /// <summary>
    /// Interface that represents conversation threads.
    /// Even though this type has 1 single UI representation most of the time,
    /// it can contain multiple IMailItem.
    /// </summary>
    public interface IMailItemThread : IMailItem
    {
        ObservableCollection<IMailItem> ThreadItems { get; }
        IMailItem LatestMailItem { get; }
        IMailItem FirstMailItem { get; }
    }
}
