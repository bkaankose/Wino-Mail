using Wino.Core.Domain.Enums;

namespace Wino.Agent.Runtime.Plan;

/// <summary>The arguments of a step, typed per action family. Ids are the device's own.</summary>
public abstract record StepArgs
{
    public sealed record NoArgs : StepArgs
    {
        public static readonly NoArgs Instance = new();
    }

    /// <summary>Mail actions: destination folder (MoveToFolder), category, language (Translate), always (Focused/Other).</summary>
    public sealed record MailChangeArgs(
        Guid? DestinationFolderId = null,
        Guid? CategoryId = null,
        string? Language = null,
        bool Always = false) : StepArgs;

    /// <summary>
    /// A new draft, reply or forward. <see cref="Send"/> is false for every CreateDraft / CreateReplyDraft: a draft
    /// opens in the composer and is never sent by the runtime. <see cref="Body"/> is composed text when the user
    /// described it and dictated text when they said it.
    /// </summary>
    public sealed record DraftArgs(
        Guid? AccountId,
        IReadOnlyList<Recipient> To,
        IReadOnlyList<Recipient> Cc,
        IReadOnlyList<Recipient> Bcc,
        string? Subject,
        DraftBody? Body,
        ReplyKind? Kind,
        string? Language,
        string? Tone,
        bool Send = false) : StepArgs;

    public sealed record EventArgs(
        Guid? CalendarId,
        string? Title,
        DateTime? Start,
        DateTime? End,
        bool AllDay,
        IReadOnlyList<Recipient> Attendees,
        string? Location,
        string? Notes,
        string? ShowAs,
        SlotRequest? Slot,
        IReadOnlyList<string> TimeAssumed) : StepArgs;

    public sealed record EventChangeArgs(
        DateTime? NewDate = null,
        DateTime? NewStart = null,
        int? ShiftMinutes = null,
        int? DurationMinutes = null,
        string? NewTitle = null,
        string? NewLocation = null,
        string? ShowAs = null,
        string? Response = null,
        string? Message = null,
        int? ReminderMinutes = null,
        bool Series = false,
        SlotRequest? Slot = null) : StepArgs;

    public sealed record TaskArgs(
        Guid? ListId,
        string? Title,
        DateTime? Due,
        bool Important,
        bool MyDay,
        string? Notes) : StepArgs;

    public sealed record TaskChangeArgs(
        string? NewTitle = null,
        string? Notes = null,
        DateTime? NewDue = null,
        bool ClearDue = false,
        bool? SetImportant = null,
        bool? SetMyDay = null,
        Guid? ToListId = null,
        string? Step = null) : StepArgs;

    public sealed record ContactChangeArgs(
        Guid? ListId = null,
        Guid? CategoryId = null,
        string? Name = null,
        string? Email = null,
        string? Phone = null) : StepArgs;

    public sealed record FolderArgs(
        Guid? AccountId = null,
        string? Name = null,
        string? NewName = null,
        Guid? ParentFolderId = null) : StepArgs;

    public sealed record SystemArgs(Guid? AccountId = null) : StepArgs;

    /// <summary>
    /// A new mail rule, fully resolved: the account it belongs to, its conditions with concrete values (addresses,
    /// domains as <c>@domain</c> suffixes, words) and its actions with folder ids. <see cref="Name"/> is the user's name
    /// for it, or null for the app to name it. The app creates the rule; the runtime never applies it.
    /// </summary>
    public sealed record MailRuleArgs(
        Guid AccountId,
        string? Name,
        MailFilterMatchMode Match,
        IReadOnlyList<MailRuleCondition> Conditions,
        IReadOnlyList<MailRuleAction> Actions) : StepArgs
    {
        public bool Deletes => Actions.Any(a => a.Type is MailFilterActionType.SoftDelete or MailFilterActionType.HardDelete);
    }

    /// <summary>One app setting switched on or off.</summary>
    public sealed record SettingArgs(AgentSetting Setting, bool Enabled) : StepArgs;

    /// <summary>A view to open; <see cref="AccountId"/> when the user named an account for it.</summary>
    public sealed record ViewArgs(AgentView View, Guid? AccountId = null) : StepArgs;
}

/// <summary><see cref="Value"/> is the invariant operand the app's rule model expects: an address, an <c>@domain</c> suffix, words, <c>true</c> / <c>false</c>, or an importance name.</summary>
public sealed record MailRuleCondition(MailFilterConditionField Field, MailFilterConditionOperator Operator, string Value);

public sealed record MailRuleAction(MailFilterActionType Type, Guid? TargetFolderId = null);

public sealed record Recipient(string Address, string? Name)
{
    public override string ToString() => string.IsNullOrEmpty(Name) ? Address : $"{Name} <{Address}>";
}

public enum ReplyKind
{
    Reply,
    ReplyAll,
    Forward,
}

public enum BodyKind
{
    /// <summary>The user's own words, verbatim.</summary>
    Dictated,
    /// <summary>Written by the compose call from the user's points.</summary>
    Composed,
    /// <summary>The compose call failed: the points themselves, one per line, for the user to edit.</summary>
    Points,
}

public sealed record DraftBody(string Text, BodyKind Kind, string? Language);

/// <summary>A free-slot request: find a gap of <see cref="Minutes"/> between <see cref="From"/> and <see cref="To"/>.</summary>
public sealed record SlotRequest(DateTime From, DateTime To, int Minutes, bool Latest, bool WithinWorkingHours, DateTime? Suggested);
