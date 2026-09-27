#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Api.Contracts.Users;

namespace Wino.Services;

/// <summary>
/// The plaintext of a sync snapshot. Every section is optional so older and newer builds can read
/// each other's documents; a missing section means "not exported". Local ids never travel:
/// per-account items name their account by address and provider, and everything else by name.
/// </summary>
internal sealed class WinoSyncSnapshotDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public DateTime ExportedAtUtc { get; set; }
    public string? PreferencesJson { get; set; }
    public List<UserMailboxSyncItemDto>? Mailboxes { get; set; }
    public List<SnapshotTemplate>? Templates { get; set; }
    public List<SnapshotShortcut>? Shortcuts { get; set; }
    public List<SnapshotFilter>? Filters { get; set; }
    public List<SnapshotCategory>? Categories { get; set; }
    public List<SnapshotAlias>? Aliases { get; set; }
    public List<SnapshotMergedInbox>? MergedInboxes { get; set; }
    public SyncSnapshotAppearance? Appearance { get; set; }
}

internal sealed class SnapshotTemplate
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string HtmlContent { get; set; } = string.Empty;
}

internal sealed class SnapshotShortcut
{
    public int Mode { get; set; }
    public string Key { get; set; } = string.Empty;
    public int ModifierKeys { get; set; }
    public int Action { get; set; }
    public bool IsEnabled { get; set; }
}

internal sealed class SnapshotFilter
{
    public string AccountAddress { get; set; } = string.Empty;
    public int ProviderType { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SourceRemoteFolderId { get; set; } = string.Empty;
    public int MatchMode { get; set; }
    public bool IsEnabled { get; set; }
    public int Sequence { get; set; }
    public bool StopProcessing { get; set; }
    public List<SnapshotFilterCondition> Conditions { get; set; } = [];
    public List<SnapshotFilterAction> Actions { get; set; } = [];
}

internal sealed class SnapshotFilterCondition
{
    public int Order { get; set; }
    public int Field { get; set; }
    public int Operator { get; set; }
    public string Value { get; set; } = string.Empty;
}

internal sealed class SnapshotFilterAction
{
    public int Order { get; set; }
    public int Type { get; set; }
    public string? TargetRemoteFolderId { get; set; }
}

internal sealed class SnapshotCategory
{
    public string AccountAddress { get; set; } = string.Empty;
    public int ProviderType { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsFavorite { get; set; }
    public string? BackgroundColorHex { get; set; }
    public string? TextColorHex { get; set; }
    public int Source { get; set; }
}

internal sealed class SnapshotAlias
{
    public string AccountAddress { get; set; } = string.Empty;
    public int ProviderType { get; set; }
    public string AliasAddress { get; set; } = string.Empty;
    public string? ReplyToAddress { get; set; }
    public string? AliasSenderName { get; set; }
}

internal sealed class SnapshotMergedInbox
{
    public string Name { get; set; } = string.Empty;
    public List<SnapshotMailboxReference> Members { get; set; } = [];
}

internal sealed class SnapshotMailboxReference
{
    public string AccountAddress { get; set; } = string.Empty;
    public int ProviderType { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WinoSyncSnapshotDocument))]
[JsonSerializable(typeof(List<UserMailboxSyncItemDto>))]
internal sealed partial class WinoSyncSnapshotJsonContext : JsonSerializerContext;
