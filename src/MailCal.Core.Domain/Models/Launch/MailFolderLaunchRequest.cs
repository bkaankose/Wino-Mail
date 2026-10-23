using System;

namespace MailCal.Core.Domain.Models.Launch;

public sealed record MailFolderLaunchRequest(Guid AccountId, Guid FolderId);
