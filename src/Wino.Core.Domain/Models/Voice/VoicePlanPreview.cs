#nullable enable
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;
using Wino.Mail.Api.Contracts.Voice;

namespace Wino.Core.Domain.Models.Voice;

/// <summary>How the commands of a group found their account.</summary>
public enum VoicePlanAccountKind
{
    /// <summary>One local account matched.</summary>
    Account,

    /// <summary>The user asked for all accounts.</summary>
    AllAccounts,

    /// <summary>No account or more than one matched what the user said; <see cref="VoicePlanPreviewGroup.Candidates"/> lists the matches.</summary>
    Unresolved,

    /// <summary>Commands that need no account, such as a command Wino can't do.</summary>
    None
}

/// <summary>
/// A planned command as the user sees it: its number within its account, in spoken order, and text with the action
/// marked strong. <see cref="CommandIndex"/> is its 0-based place in the plan, which chained commands refer to.
/// </summary>
public sealed record VoicePlanPreviewStep(int Number, string MarkedText, string? Detail, VoiceCommand Command, int CommandIndex);

/// <summary>A part of the request Wino can't do, with the reason in the app's words. It is not a step and never runs.</summary>
public sealed record VoicePlanPreviewIssue(string Message, VoiceCommand Command);

/// <summary>
/// The commands that act on one account (or on all of them, or on an account Wino could not tell), in the order the
/// user said them, and the parts of the request for that account Wino can't do. <see cref="SpokenAccount"/> is how
/// the user named the account, for an unresolved group.
/// </summary>
public sealed record VoicePlanPreviewGroup(
    VoicePlanAccountKind Kind,
    MailAccount? Account,
    IReadOnlyList<MailAccount> Candidates,
    string? SpokenAccount,
    IReadOnlyList<VoicePlanPreviewStep> Steps,
    IReadOnlyList<VoicePlanPreviewIssue> Issues);

/// <summary>A voice plan grouped by the local account each command acts on.</summary>
public sealed record VoicePlanPreview(IReadOnlyList<VoicePlanPreviewGroup> Groups);
