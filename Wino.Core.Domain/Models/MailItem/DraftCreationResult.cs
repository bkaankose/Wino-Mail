using System;

namespace Wino.Core.Domain.Models.MailItem;

/// <summary>
/// Compact result returned after the companion creates a local draft.
/// </summary>
public sealed record DraftCreationResult(Guid DraftMailUniqueId, string MimeFilePath);
