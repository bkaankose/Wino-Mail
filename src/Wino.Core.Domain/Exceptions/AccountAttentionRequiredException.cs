using System;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Exceptions;

/// <summary>
/// Thrown by a provider transport when it refuses network work for an account that needs attention.
/// The account must be fixed by the user first; retrying cannot succeed.
/// </summary>
public class AccountAttentionRequiredException : Exception
{
    public AccountAttentionRequiredException(MailAccount account)
        : base($"Network access for account {account?.Id} is paused until its {account?.AttentionReason} issue is fixed.")
    {
        Account = account;
        AttentionReason = account?.AttentionReason ?? AccountAttentionReason.None;
    }

    public MailAccount Account { get; }
    public AccountAttentionReason AttentionReason { get; }
}
