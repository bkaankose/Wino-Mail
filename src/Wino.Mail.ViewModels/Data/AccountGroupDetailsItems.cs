using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Mail.ViewModels.Data;

/// <summary>Opens the account group page; no group id creates a new group.</summary>
public sealed record AccountGroupDetailsNavigationContext(Guid? GroupId, string? Name = null);

/// <summary>A color choice on the account group page. The default choice has no key and uses the theme colors.</summary>
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

    public override string ToString() => DisplayName;
}

/// <summary>
/// An icon choice on the account group page. It wraps the enum so list controls never box it, and follows the
/// selected color so the choices preview the group's tint.
/// </summary>
public sealed partial class AccountGroupIconOption : ObservableObject
{
    public AccountGroupIconOption(AccountGroupIcon icon)
    {
        Icon = icon;
    }

    public AccountGroupIcon Icon { get; }

    public WinoIconGlyph Glyph => AccountGroupIcons.GetGlyph(Icon);

    public string DisplayName => AccountGroupIcons.GetDisplayName(Icon);

    [ObservableProperty]
    public partial string? ColorKey { get; set; }

    public override string ToString() => DisplayName;
}

/// <summary>An account on the account group page.</summary>
public sealed class AccountGroupEntryViewModel
{
    public AccountGroupEntryViewModel(MailAccount account, MergedInbox? currentGroup)
    {
        Id = account.Id;
        Account = account;
        Title = account.Name ?? string.Empty;
        Description = account.Address ?? string.Empty;
        CurrentGroup = currentGroup;
    }

    public Guid Id { get; }

    public MailAccount Account { get; }

    public string Title { get; }

    public string Description { get; }

    /// <summary>The group the account is in now, before this page saves.</summary>
    public MergedInbox? CurrentGroup { get; }

    /// <summary>Set while the account is listed as available and belongs to another group.</summary>
    public string? OtherGroupName { get; private set; }

    public bool IsInOtherGroup => !string.IsNullOrEmpty(OtherGroupName);

    public string OtherGroupText => IsInOtherGroup ? string.Format(Translator.AccountGroupDetailsPage_InOtherGroup, OtherGroupName) : string.Empty;

    public string AddAutomationName => string.Format(Translator.AccountGroupDetailsPage_AddAutomationName, Title);

    public string RemoveAutomationName => string.Format(Translator.AccountGroupDetailsPage_RemoveAutomationName, Title);

    /// <summary>Marks the account as belonging to another group than the edited one.</summary>
    public void SetEditedGroup(Guid? editedGroupId)
        => OtherGroupName = CurrentGroup is { } group && group.Id != editedGroupId ? group.Name : null;
}
