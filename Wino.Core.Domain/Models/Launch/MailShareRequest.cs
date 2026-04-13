using System;
using System.Collections.Generic;
using Wino.Core.Domain.Models.Common;

namespace Wino.Core.Domain.Models.Launch;

public sealed class MailShareRequest
{
    public MailShareRequest(IReadOnlyList<SharedFile> files)
    {
        Files = files ?? throw new ArgumentNullException(nameof(files));
    }

    public IReadOnlyList<SharedFile> Files { get; }
}
