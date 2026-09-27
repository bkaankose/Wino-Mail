using System;

namespace Wino.Core.Domain.Models.Navigation;

/// <summary>
/// Shown in the mail page area when the selected account has no Inbox to open yet: it was
/// restored from a backup and needs a sign-in, or its first sync has not brought folders in.
/// </summary>
public sealed record MailAccountIdleState(Guid AccountId, string AccountName, bool NeedsSignIn);
