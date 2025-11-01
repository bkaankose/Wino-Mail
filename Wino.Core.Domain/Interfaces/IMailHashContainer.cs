using System;
using System.Collections.Generic;

namespace Wino.Core.Domain.Interfaces;

public interface IMailHashContainer
{
    IEnumerable<Guid> GetContainingIds();
}
