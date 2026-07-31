using System.Collections.Generic;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Models.Synchronization;

public record OnlineSearchResult(List<MailCopy> SearchResult);
