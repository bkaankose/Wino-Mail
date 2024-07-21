using System;
using System.Collections.Generic;

namespace Wino.Domain.Models.MailItem
{
    /// <summary>
    /// An interface that returns the UniqueId store for IMailItem.
    /// For threads, it may be multiple items.
    /// For single mails, it'll always be one item.
    /// </summary>
    public interface IMailHashContainer
    {
        IEnumerable<Guid> GetContainingIds();
    }
}
