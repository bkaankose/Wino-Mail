using System;
using System.Collections.Generic;

namespace MailCal.Core.Domain.Interfaces;

public interface IMailHashContainer
{
    IEnumerable<Guid> GetContainingIds();
}
