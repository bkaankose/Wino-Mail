using System.Collections.Generic;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Messaging.UI;

/// <summary>Asks the open mail list to run a search over <paramref name="Folders"/>, as if the user had typed it.</summary>
public sealed record MailSearchRequested(MailSearchCriteria Criteria, IReadOnlyList<IMailItemFolder> Folders);
