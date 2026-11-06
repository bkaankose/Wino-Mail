#nullable enable
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.ViewModels.Data;

/// <summary>A sender rule policy as the rules page lists it. A null policy is the "All policies" filter.</summary>
public sealed record RemoteContentPolicyOption(RemoteContentSenderPolicy? Policy, string Title, string Legend)
{
    public static RemoteContentPolicyOption[] CreatePolicies() =>
    [
        new(RemoteContentSenderPolicy.LoadEverything, Translator.RemoteContentRules_PolicyLoadEverything, Translator.RemoteContentRules_LegendLoadEverything),
        new(RemoteContentSenderPolicy.ImagesOnly, Translator.RemoteContentRules_PolicyImagesOnly, Translator.RemoteContentRules_LegendImagesOnly),
        new(RemoteContentSenderPolicy.Block, Translator.RemoteContentRules_PolicyBlock, Translator.RemoteContentRules_LegendBlock)
    ];

    public static RemoteContentPolicyOption[] CreateFilters(RemoteContentPolicyOption[] policies)
        => [new(null, Translator.RemoteContentRules_AllPolicies, string.Empty), .. policies];
}

/// <summary>One row of the sender rules page.</summary>
public sealed partial class RemoteContentRuleViewModel : ObservableObject
{
    private readonly Func<RemoteContentRuleViewModel, RemoteContentSenderPolicy, Task> _changePolicy;
    private readonly Func<RemoteContentRuleViewModel, Task> _remove;
    private bool _isSyncingPolicy;

    public RemoteContentRuleViewModel(RemoteContentSenderRule rule,
                                      string? contactName,
                                      RemoteContentPolicyOption[] policyOptions,
                                      Func<RemoteContentRuleViewModel, RemoteContentSenderPolicy, Task> changePolicy,
                                      Func<RemoteContentRuleViewModel, Task> remove)
    {
        Rule = rule;
        ContactName = string.IsNullOrWhiteSpace(contactName) ? null : contactName.Trim();
        PolicyOptions = policyOptions;
        _changePolicy = changePolicy;
        _remove = remove;

        SyncPolicy();
    }

    public RemoteContentSenderRule Rule { get; private set; }

    public RemoteContentPolicyOption[] PolicyOptions { get; }

    public string? ContactName { get; }

    public Guid Id => Rule.Id;
    public string Pattern => Rule.Pattern;
    public bool IsDomain => Rule.Kind == RemoteContentRuleKind.Domain;
    public bool IsAddress => !IsDomain;
    public RemoteContentSenderPolicy Policy => Rule.Policy;

    /// <summary>The contact's name for an address with a stored contact, otherwise the pattern itself.</summary>
    public string DisplayName => ContactName ?? Pattern;

    /// <summary>The address under a contact name, the domain explanation for a domain rule, otherwise empty.</summary>
    public string DetailText => IsDomain
        ? string.Format(Translator.RemoteContentRules_DomainDescription, Pattern)
        : ContactName == null ? string.Empty : Pattern;

    public bool HasDetailText => DetailText.Length > 0;

    /// <summary>Two letters for an address avatar; domain rows show a globe instead.</summary>
    public string Initials => IsDomain ? string.Empty : GetInitials(DisplayName);

    public string KindText => IsDomain ? Translator.RemoteContentRules_KindDomain : Translator.RemoteContentRules_KindAddress;

    public string AddedText => string.Format(
        Rule.Source == RemoteContentRuleSource.Reader ? Translator.RemoteContentRules_SourceReader : Translator.RemoteContentRules_SourceManual,
        Rule.CreatedUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture));

    public string PolicyText => SelectedPolicyOption?.Title ?? string.Empty;

    public string PolicyAutomationName => $"{Translator.RemoteContentRules_ColumnPolicy}, {Pattern}";

    public string DeleteAutomationName => string.Format(Translator.RemoteContentRules_DeleteRule, Pattern);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PolicyText))]
    public partial RemoteContentPolicyOption? SelectedPolicyOption { get; set; }

    partial void OnSelectedPolicyOptionChanged(RemoteContentPolicyOption? value)
    {
        if (_isSyncingPolicy || value?.Policy is not { } policy || policy == Rule.Policy)
            return;

        SetPolicyCommand.Execute(policy);
    }

    [RelayCommand]
    private Task SetPolicyAsync(RemoteContentSenderPolicy policy)
        => policy == Rule.Policy ? Task.CompletedTask : _changePolicy(this, policy);

    [RelayCommand]
    private Task RemoveAsync() => _remove(this);

    /// <summary>Takes a saved or reloaded rule without writing it back.</summary>
    public void Update(RemoteContentSenderRule rule)
    {
        Rule = rule;

        SyncPolicy();
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Puts the picker back on the stored policy, for example after a failed write.</summary>
    public void SyncPolicy()
    {
        _isSyncingPolicy = true;

        try
        {
            SelectedPolicyOption = PolicyOptions.FirstOrDefault(option => option.Policy == Rule.Policy);
        }
        finally
        {
            _isSyncingPolicy = false;
        }
    }

    public bool Matches(string? filter, RemoteContentSenderPolicy? policyFilter)
    {
        if (policyFilter is { } policy && Rule.Policy != policy)
            return false;

        if (string.IsNullOrWhiteSpace(filter))
            return true;

        var term = filter.Trim();

        return Pattern.Contains(term, StringComparison.OrdinalIgnoreCase)
               || (ContactName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string GetInitials(string name)
    {
        var source = name.Contains('@') ? name[..name.IndexOf('@')] : name;
        var parts = source.Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
            return "?";

        return parts.Length == 1
            ? parts[0][..Math.Min(2, parts[0].Length)].ToUpper(CultureInfo.CurrentCulture)
            : $"{parts[0][0]}{parts[^1][0]}".ToUpper(CultureInfo.CurrentCulture);
    }
}
