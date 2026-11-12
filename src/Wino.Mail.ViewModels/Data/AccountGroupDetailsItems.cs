using System;
using System.Linq;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Mail.ViewModels.Data;

/// <summary>Opens the account group page; no group id creates a new group.</summary>
public sealed record AccountGroupDetailsNavigationContext(Guid? GroupId);

/// <summary>A text color choice on the account group page. The default choice has no key.</summary>
public sealed class AccountGroupColorOption
{
    public AccountGroupColorOption(AccountGroupColor? color)
    {
        Color = color;
    }

    public AccountGroupColor? Color { get; }

    public string? Key => Color?.Key;

    public bool IsDefault => Color is null;

    public string DisplayName => Color?.DisplayName ?? Translator.AccountGroupColor_Default;

    public string? LightHex => Color?.LightHex;

    public string? DarkHex => Color?.DarkHex;

    public override string ToString() => DisplayName;
}

/// <summary>An account or linked account on the account group page.</summary>
public sealed class AccountGroupEntryViewModel
{
    public AccountGroupEntryViewModel(AccountLayoutEntry entry, MailAccountGroup? currentGroup)
    {
        Id = entry.Id;
        IsLinkedAccount = entry.Kind == AccountLayoutEntryKind.LinkedAccount;
        Account = entry.Account;
        Title = entry.Name;
        Description = IsLinkedAccount
            ? string.Format(Translator.AccountGroup_LinkedAccountDescription, string.Join(", ", entry.Members.Select(member => member.Address)))
            : entry.Account?.Address ?? string.Empty;
        CurrentGroup = currentGroup;
    }

    /// <summary>The account id, or the merged inbox id of a linked account.</summary>
    public Guid Id { get; }

    public bool IsLinkedAccount { get; }

    /// <summary>The account of a single-account entry, for its icon.</summary>
    public MailAccount? Account { get; }

    public string Title { get; }

    public string Description { get; }

    /// <summary>The group the entry is in now, before this page saves.</summary>
    public MailAccountGroup? CurrentGroup { get; }

    /// <summary>Set while the entry is listed as available and belongs to another group.</summary>
    public string? OtherGroupName { get; private set; }

    public bool IsInOtherGroup => !string.IsNullOrEmpty(OtherGroupName);

    public string OtherGroupText => IsInOtherGroup ? string.Format(Translator.AccountGroupDetailsPage_InOtherGroup, OtherGroupName) : string.Empty;

    public string AddActionText => IsInOtherGroup ? Translator.AccountGroupDetailsPage_Move : Translator.Buttons_Add;

    public string AddAutomationName => string.Format(
        IsInOtherGroup ? Translator.AccountGroupDetailsPage_MoveAutomationName : Translator.AccountGroupDetailsPage_AddAutomationName,
        Title);

    public string RemoveAutomationName => string.Format(Translator.AccountGroupDetailsPage_RemoveAutomationName, Title);

    public string MoveUpAutomationName => string.Format(Translator.Accessibility_ItemAction, Translator.AccountGroupDetailsPage_MoveUp, Title);

    public string MoveDownAutomationName => string.Format(Translator.Accessibility_ItemAction, Translator.AccountGroupDetailsPage_MoveDown, Title);

    /// <summary>Marks the entry as belonging to another group than the edited one.</summary>
    public void SetEditedGroup(Guid? editedGroupId)
        => OtherGroupName = CurrentGroup is { } group && group.Id != editedGroupId ? group.Name : null;
}
