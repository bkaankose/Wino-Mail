using System;

namespace Wino.Core.Domain.Models.Launch;

public sealed record MailFolderLaunchRequest(Guid AccountId, Guid FolderId);
