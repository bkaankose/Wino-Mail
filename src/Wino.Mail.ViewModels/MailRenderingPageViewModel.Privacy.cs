#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MimeKit;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Reader;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.UI;
using Wino.Services;

namespace Wino.Mail.ViewModels;

public partial class MailRenderingPageViewModel
{
    private const int PrivacyHostListLimit = 3;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrivacyDecision))]
    public partial RemoteContentDecision? CurrentDecision { get; set; }

    [ObservableProperty]
    public partial int BlockedTrackerCount { get; set; }

    [ObservableProperty]
    public partial int BlockedResourceCount { get; set; }

    [ObservableProperty]
    public partial List<BlockedRemoteResource> BlockedResources { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrivacyBlockedHosts))]
    public partial List<PrivacyBlockedHost> PrivacyBlockedHosts { get; set; } = [];

    /// <summary>Block all withheld remote content and neither Junk nor a Block rule explains why.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRemoteContentBarVisible))]
    public partial bool IsRemoteContentWithheld { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRemoteContentBarVisible))]
    public partial bool IsJunkBlocked { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRemoteContentBarVisible))]
    public partial bool IsSenderBlockedByRule { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPrivacyCountVisible))]
    [NotifyPropertyChangedFor(nameof(PrivacyButtonIcon))]
    public partial PrivacyButtonState PrivacyState { get; set; }

    [ObservableProperty]
    public partial string PrivacyButtonAutomationName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PrivacyPolicyLine { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PrivacyReasonLine { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrivacyDetailLine))]
    public partial string PrivacyDetailLine { get; set; } = string.Empty;

    /// <summary>Message of the withheld bar, including the note that the sender can't tell.</summary>
    [ObservableProperty]
    public partial string WithheldBarMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RuleBarTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RuleBarMessage { get; set; } = string.Empty;

    /// <summary>The flyout's actions in order. Heads draw a separator before items whose StartsGroup is set.</summary>
    public ObservableCollection<PrivacyMenuItem> PrivacyMenuItems { get; } = [];

    public bool HasPrivacyDecision => CurrentDecision != null;
    public bool HasPrivacyBlockedHosts => PrivacyBlockedHosts.Count > 0;
    public bool HasPrivacyDetailLine => !string.IsNullOrEmpty(PrivacyDetailLine);
    public bool IsPrivacyCountVisible => PrivacyState == PrivacyButtonState.Count;
    public bool IsRemoteContentBarVisible => IsRemoteContentWithheld || IsJunkBlocked || IsSenderBlockedByRule;
    public WinoIconGlyph PrivacyButtonIcon => PrivacyState == PrivacyButtonState.Blocked ? WinoIconGlyph.ShieldError : WinoIconGlyph.Shield;

    private async Task<RemoteContentDecision> ResolveRemoteContentDecisionAsync(MailCopy? mailCopy, MimeMessage message, string? messagePath, bool isJunk)
    {
        try
        {
            return await _remoteContentPolicyService.ResolveAsync(mailCopy, message, mailCopy == null ? messagePath : null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Resolving the remote content policy failed. Falling back to the default mode.");

            var senderAddress = message.From?.Mailboxes.FirstOrDefault()?.Address ?? string.Empty;
            return new RemoteContentDecision(
                isJunk ? RemoteContentMode.BlockAll : PreferencesService.RemoteContentMode,
                isJunk ? RemoteContentDecisionReason.Junk : RemoteContentDecisionReason.Mode,
                null,
                senderAddress,
                RemoteContentRulePattern.GetDomain(senderAddress));
        }
    }

    private Guid? GetRemoteContentSessionId()
    {
        if (initializedMailItemViewModel?.MailCopy is MailCopy mailCopy)
            return mailCopy.UniqueId;

        var path = initializedMimeMessageInformation?.Path;
        return string.IsNullOrWhiteSpace(path) ? null : _remoteContentSessionService.GetFileId(path);
    }

    private void ResetPrivacyState()
    {
        CurrentDecision = null;
        BlockedTrackerCount = 0;
        BlockedResourceCount = 0;
        BlockedResources = [];
        PrivacyBlockedHosts = [];
        IsRemoteContentWithheld = false;
        IsJunkBlocked = false;
        IsSenderBlockedByRule = false;
        PrivacyState = PrivacyButtonState.Plain;
        PrivacyButtonAutomationName = Translator.Reader_Privacy_ButtonPlain;
        PrivacyPolicyLine = string.Empty;
        PrivacyReasonLine = string.Empty;
        PrivacyDetailLine = string.Empty;
        WithheldBarMessage = string.Empty;
        RuleBarTitle = string.Empty;
        RuleBarMessage = string.Empty;
        PrivacyMenuItems.Clear();
    }

    private void ApplyPrivacyState(MailRenderModel renderModel, RemoteContentDecision decision, bool isJunk, bool isTransient)
    {
        var blocked = renderModel.BlockedResources.ToList();
        var trackers = blocked.Where(IsTracker).ToList();
        var blockedImageCount = blocked.Count - trackers.Count;
        var hosts = blocked.Select(r => r.Host).Where(h => !string.IsNullOrEmpty(h)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var mode = decision.EffectiveMode;
        var rule = isTransient || decision.Reason is not (RemoteContentDecisionReason.AddressRule or RemoteContentDecisionReason.DomainRule)
            ? null
            : decision.Rule;
        var hasRuleLoadingEverything = mode == RemoteContentMode.LoadEverything && !isTransient &&
            decision.Reason is RemoteContentDecisionReason.AddressRule or RemoteContentDecisionReason.DomainRule
                or RemoteContentDecisionReason.Contact or RemoteContentDecisionReason.Recipient;

        CurrentDecision = decision;
        BlockedResources = blocked;
        BlockedResourceCount = blocked.Count;
        BlockedTrackerCount = trackers.Count;
        PrivacyBlockedHosts = mode == RemoteContentMode.BlockTrackers ? GroupBlockedHosts(trackers) : [];

        IsJunkBlocked = isJunk && mode == RemoteContentMode.BlockAll;
        IsSenderBlockedByRule = !IsJunkBlocked && mode == RemoteContentMode.BlockAll && rule?.Policy == RemoteContentSenderPolicy.Block;
        IsRemoteContentWithheld = !IsJunkBlocked && !IsSenderBlockedByRule && mode == RemoteContentMode.BlockAll &&
            (blocked.Count > 0 || renderModel.RemoteImageCount > 0);

        PrivacyState = mode == RemoteContentMode.BlockAll
            ? PrivacyButtonState.Blocked
            : hasRuleLoadingEverything
                ? PrivacyButtonState.Rule
                : trackers.Count > 0 ? PrivacyButtonState.Count : PrivacyButtonState.Plain;

        PrivacyButtonAutomationName = PrivacyState switch
        {
            PrivacyButtonState.Count => trackers.Count == 1
                ? Translator.Reader_Privacy_ButtonCountOne
                : string.Format(Translator.Reader_Privacy_ButtonCount, trackers.Count),
            PrivacyButtonState.Blocked => Translator.Reader_Privacy_ButtonBlocked,
            PrivacyButtonState.Rule => Translator.Reader_Privacy_ButtonRule,
            _ => Translator.Reader_Privacy_ButtonPlain
        };

        PrivacyPolicyLine = mode switch
        {
            RemoteContentMode.LoadEverything => Translator.Reader_Privacy_PolicyLoadedEverything,
            RemoteContentMode.BlockTrackers => trackers.Count switch
            {
                0 => Translator.Reader_Privacy_PolicyTrackersBlocked,
                1 => Translator.Reader_Privacy_PolicyOneTrackerBlocked,
                _ => string.Format(Translator.Reader_Privacy_PolicyTrackersBlockedCount, trackers.Count)
            },
            _ => Translator.Reader_Privacy_PolicyRemoteContentBlocked
        };

        PrivacyReasonLine = GetReasonLine(decision, mode, trackers.Count);
        PrivacyDetailLine = GetDetailLine(decision, mode, isJunk, isTransient, renderModel.RemoteImageCount, blockedImageCount, trackers.Count, hosts);

        var withheldMessage = hosts.Count == 1
            ? string.Format(Translator.Reader_Privacy_WithheldMessageOneSite, blocked.Count)
            : string.Format(Translator.Reader_Privacy_WithheldMessage, blocked.Count, hosts.Count);
        WithheldBarMessage = $"{withheldMessage} {Translator.Reader_Privacy_WithheldNote}";
        RuleBarTitle = rule == null ? string.Empty : string.Format(Translator.Reader_Privacy_RuleBarTitle, rule.Pattern);
        RuleBarMessage = trackers.Count == 0
            ? string.Format(Translator.Reader_Privacy_RuleBarImagesMessage, blockedImageCount)
            : string.Format(Translator.Reader_Privacy_RuleBarMessage, blockedImageCount, trackers.Count);

        RebuildPrivacyMenu(decision, rule, isJunk, isTransient, trackers.Count);
    }

    private string GetReasonLine(RemoteContentDecision decision, RemoteContentMode mode, int trackerCount)
    {
        return decision.Reason switch
        {
            RemoteContentDecisionReason.Junk => Translator.Reader_Privacy_ReasonJunk,
            RemoteContentDecisionReason.SessionOverride => mode == RemoteContentMode.BlockAll
                ? Translator.Reader_Privacy_ReasonSessionBlocked
                : Translator.Reader_Privacy_ReasonSessionLoaded,
            RemoteContentDecisionReason.AddressRule or RemoteContentDecisionReason.DomainRule when decision.Rule != null
                => string.Format(Translator.Reader_Privacy_ReasonRule, decision.Rule.Pattern, GetRuleSuffix(decision.Rule)),
            RemoteContentDecisionReason.Contact => string.Format(Translator.Reader_Privacy_ReasonContact, decision.SenderAddress),
            RemoteContentDecisionReason.Recipient => string.Format(Translator.Reader_Privacy_ReasonRecipient, decision.SenderAddress),
            _ => mode switch
            {
                RemoteContentMode.BlockTrackers when trackerCount == 0 => Translator.Reader_Privacy_ReasonModeNoneFound,
                RemoteContentMode.BlockAll => Translator.Reader_Privacy_ReasonModeNoRule,
                _ => Translator.Reader_Privacy_ReasonMode
            }
        };
    }

    private static string GetRuleSuffix(RemoteContentSenderRule rule) => rule.Policy switch
    {
        RemoteContentSenderPolicy.Block => Translator.RemoteContentRules_PolicyBlock,
        RemoteContentSenderPolicy.ImagesOnly => Translator.RemoteContentRules_PolicyImagesOnly,
        _ => string.Format(Translator.Reader_Privacy_RuleAdded,
            rule.CreatedUtc.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture))
    };

    private string GetDetailLine(RemoteContentDecision decision,
                                 RemoteContentMode mode,
                                 bool isJunk,
                                 bool isTransient,
                                 int remoteImageCount,
                                 int blockedImageCount,
                                 int trackerCount,
                                 List<string> hosts)
    {
        switch (mode)
        {
            case RemoteContentMode.BlockAll:
                if (blockedImageCount + trackerCount == 0)
                    return string.Empty;

                if (isJunk)
                    return trackerCount == 0
                        ? string.Format(Translator.Reader_Privacy_DetailJunkImagesNotLoaded, blockedImageCount)
                        : string.Format(Translator.Reader_Privacy_DetailJunkNotLoaded, blockedImageCount, trackerCount);

                var hostList = FormatHostList(hosts);

                return trackerCount > 0
                    ? string.Format(Translator.Reader_Privacy_DetailNotLoaded, blockedImageCount, trackerCount, hostList)
                    : string.Format(Translator.Reader_Privacy_DetailImagesNotLoaded, blockedImageCount, hostList);

            case RemoteContentMode.BlockTrackers:
                if (trackerCount > 0)
                    return Translator.Reader_Privacy_DetailTrackersExplanation;

                var loaded = remoteImageCount switch
                {
                    0 => string.Empty,
                    1 => string.Format(Translator.Reader_Privacy_DetailOneImageLoaded, decision.SenderDomain),
                    _ => string.Format(Translator.Reader_Privacy_DetailImagesLoaded, remoteImageCount, decision.SenderDomain)
                };

                return string.IsNullOrEmpty(loaded)
                    ? Translator.Reader_Privacy_DetailNoTrackersFound
                    : $"{loaded} {Translator.Reader_Privacy_DetailNoTrackersFound}";

            default:
                if (!isTransient && decision.Rule != null &&
                    decision.Reason is RemoteContentDecisionReason.AddressRule or RemoteContentDecisionReason.DomainRule &&
                    PreferencesService.RemoteContentMode != RemoteContentMode.LoadEverything)
                {
                    return string.Format(Translator.Reader_Privacy_DetailRuleOverridesMode,
                        GetModeTitle(PreferencesService.RemoteContentMode),
                        GetPolicyTitle(decision.Rule.Policy));
                }

                return Translator.Reader_Privacy_DetailNothingBlocked;
        }
    }

    private static string GetModeTitle(RemoteContentMode mode) => mode switch
    {
        RemoteContentMode.LoadEverything => Translator.PrivacySecurity_ModeLoadEverything_Title,
        RemoteContentMode.BlockTrackers => Translator.PrivacySecurity_ModeBlockTrackers_Title,
        _ => Translator.PrivacySecurity_ModeBlockAll_Title
    };

    private static string GetPolicyTitle(RemoteContentSenderPolicy policy) => policy switch
    {
        RemoteContentSenderPolicy.LoadEverything => Translator.RemoteContentRules_PolicyLoadEverything,
        RemoteContentSenderPolicy.ImagesOnly => Translator.RemoteContentRules_PolicyImagesOnly,
        _ => Translator.RemoteContentRules_PolicyBlock
    };

    private static string FormatHostList(List<string> hosts)
    {
        var shown = hosts.Take(PrivacyHostListLimit).ToList();

        return shown.Count switch
        {
            0 => string.Empty,
            1 => shown[0],
            _ => string.Format(Translator.Reader_Privacy_ListAnd, string.Join(", ", shown.Take(shown.Count - 1)), shown[^1])
        };
    }

    private static bool IsTracker(BlockedRemoteResource resource)
        => resource.Reason != BlockedResourceReason.RemoteContent ||
           (!string.IsNullOrEmpty(resource.Host) && TrackerDetector.IsKnownTrackerHost(resource.Host));

    private static List<PrivacyBlockedHost> GroupBlockedHosts(List<BlockedRemoteResource> trackers)
    {
        var hosts = new List<PrivacyBlockedHost>();

        foreach (var group in trackers.GroupBy(r => (Host: string.IsNullOrEmpty(r.Host) ? r.Url : r.Host, r.Reason)))
        {
            var reasonText = group.Key.Reason switch
            {
                BlockedResourceReason.TinyImage => Translator.Reader_Privacy_ResourceTinyImage,
                BlockedResourceReason.HiddenImage => Translator.Reader_Privacy_ResourceHiddenImage,
                BlockedResourceReason.TrackingPath => Translator.Reader_Privacy_ResourceTrackingPath,
                _ => Translator.Reader_Privacy_ResourceKnownTracker
            };

            var count = group.Count();
            var description = count > 1 ? string.Format(Translator.Reader_Privacy_ResourceCount, reasonText, count) : reasonText;

            hosts.Add(new PrivacyBlockedHost(group.Key.Host, description, count));
        }

        return hosts;
    }

    /// <summary>
    /// The domain a domain rule from the reader targets: the registrable part of the sender's domain,
    /// so <c>news.temu.com</c> gives <c>temu.com</c> and <c>mail.contoso.co.uk</c> gives <c>contoso.co.uk</c>.
    /// </summary>
    private static string GetRuleDomain(string senderDomain)
    {
        var candidates = RemoteContentRulePattern.GetDomainCandidates(senderDomain);

        if (candidates.Count == 0)
            return senderDomain;

        var shortest = candidates[^1];
        var labels = shortest.Split('.');

        // A rough public suffix check: a two-letter country code after a short second-level label
        // (co.uk, com.au) is a suffix shared by unrelated senders, never a rule target.
        if (candidates.Count > 1 && labels.Length == 2 && labels[1].Length == 2 && labels[0].Length <= 3)
            return candidates[^2];

        return shortest;
    }

    private void RebuildPrivacyMenu(RemoteContentDecision decision, RemoteContentSenderRule? rule, bool isJunk, bool isTransient, int trackerCount)
    {
        var items = new List<PrivacyMenuItem>();
        var mode = decision.EffectiveMode;
        var address = decision.SenderAddress;
        var domain = GetRuleDomain(decision.SenderDomain);
        var hasAddress = !string.IsNullOrEmpty(address);
        var hasDomain = !string.IsNullOrEmpty(domain);

        PrivacyMenuItem LoadOnce(string hint, bool recommended = false)
            => new(PrivacyMenuAction.LoadOnce, Translator.Reader_Privacy_LoadOnce, hint, WinoIconGlyph.ArrowClockwise, IsRecommended: recommended);
        PrivacyMenuItem LoadTrackersOnce()
            => new(PrivacyMenuAction.LoadTrackersOnce, Translator.Reader_Privacy_LoadThemOnce, Translator.Reader_Privacy_UntilWinoCloses, WinoIconGlyph.ArrowClockwise);
        PrivacyMenuItem BlockOnce()
            => new(PrivacyMenuAction.BlockOnce, Translator.Reader_Privacy_BlockForMessage, null, WinoIconGlyph.ShieldError);
        PrivacyMenuItem NeverLoad()
            => new(PrivacyMenuAction.NeverLoadFromSender, Translator.Reader_Privacy_NeverLoadFromSender, null, WinoIconGlyph.SpecialFolderJunk);
        PrivacyMenuItem ImagesOnlyFromDomain()
            => new(PrivacyMenuAction.ImagesOnlyFromDomain, string.Format(Translator.Reader_Privacy_ImagesOnlyFrom, domain), Translator.Reader_Privacy_NoTrackers, WinoIconGlyph.Image);

        if (isJunk)
        {
            items.Add(mode == RemoteContentMode.BlockAll ? LoadOnce(Translator.Reader_Privacy_UntilWinoCloses) : BlockOnce());
        }
        else
        {
            if (rule != null)
            {
                if (mode == RemoteContentMode.BlockAll)
                    items.Add(LoadOnce(Translator.Reader_Privacy_NotRemembered));
                else if (mode == RemoteContentMode.BlockTrackers && trackerCount > 0)
                    items.Add(LoadTrackersOnce());
                else
                    items.Add(BlockOnce());

                if (rule.Policy == RemoteContentSenderPolicy.ImagesOnly)
                    items.Add(new(PrivacyMenuAction.ChangeRuleToLoadEverything, Translator.Reader_Privacy_ChangeRuleToLoadEverything, null, WinoIconGlyph.Globe));
                else
                    items.Add(new(PrivacyMenuAction.ChangeRuleToImagesOnly, Translator.Reader_Privacy_ChangeRuleToImagesOnly, null, WinoIconGlyph.Image));

                items.Add(new(PrivacyMenuAction.RemoveRule, Translator.Reader_Privacy_RemoveRule, null, WinoIconGlyph.Delete));
            }
            else if (isTransient)
            {
                var appliedRule = decision.Rule;

                if (appliedRule != null)
                {
                    items.Add(new(PrivacyMenuAction.ChangeRuleToImagesOnly, Translator.Reader_Privacy_ChangeRuleToImagesOnly, null, WinoIconGlyph.Image));
                    items.Add(new(PrivacyMenuAction.RemoveRule, Translator.Reader_Privacy_RemoveRule, null, WinoIconGlyph.Delete));
                }
            }
            else
            {
                switch (mode)
                {
                    case RemoteContentMode.BlockAll:
                        items.Add(LoadOnce(Translator.Reader_Privacy_UntilWinoCloses, recommended: true));

                        if (hasAddress)
                            items.Add(new(PrivacyMenuAction.AlwaysLoadFromAddress, string.Format(Translator.Reader_Privacy_AlwaysLoadFrom, address), null, WinoIconGlyph.Person));

                        if (hasDomain)
                        {
                            items.Add(new(PrivacyMenuAction.AlwaysLoadFromDomain, string.Format(Translator.Reader_Privacy_AlwaysLoadFrom, domain), null, WinoIconGlyph.Globe));
                            items.Add(ImagesOnlyFromDomain());
                        }
                        break;
                    case RemoteContentMode.BlockTrackers:
                        items.Add(trackerCount > 0 ? LoadTrackersOnce() : BlockOnce());

                        if (hasDomain)
                            items.Add(new(PrivacyMenuAction.AlwaysLoadFromDomain, string.Format(Translator.Reader_Privacy_AlwaysLoadEverythingFrom, domain), null, WinoIconGlyph.Globe));
                        break;
                    default:
                        items.Add(BlockOnce());

                        if (hasDomain)
                            items.Add(ImagesOnlyFromDomain());
                        break;
                }

                if (hasAddress)
                    items.Add(NeverLoad());
            }

            items.Add(new(PrivacyMenuAction.ManageRules, Translator.Reader_Privacy_ManageRules, null, WinoIconGlyph.Settings, StartsGroup: items.Count > 0));
        }

        PrivacyMenuItems.Clear();

        foreach (var item in items)
            PrivacyMenuItems.Add(item);
    }

    [RelayCommand]
    private Task InvokePrivacyMenuItemAsync(PrivacyMenuItem? item) => item?.Action switch
    {
        PrivacyMenuAction.LoadOnce or PrivacyMenuAction.LoadTrackersOnce => LoadOnceAsync(),
        PrivacyMenuAction.BlockOnce => BlockOnceAsync(),
        PrivacyMenuAction.AlwaysLoadFromAddress => AddSenderRuleAsync(new SenderRuleRequest(RemoteContentSenderPolicy.LoadEverything, RemoteContentRuleKind.Address)),
        PrivacyMenuAction.AlwaysLoadFromDomain => AddSenderRuleAsync(new SenderRuleRequest(RemoteContentSenderPolicy.LoadEverything, RemoteContentRuleKind.Domain)),
        PrivacyMenuAction.ImagesOnlyFromDomain => AddSenderRuleAsync(new SenderRuleRequest(RemoteContentSenderPolicy.ImagesOnly, RemoteContentRuleKind.Domain)),
        PrivacyMenuAction.NeverLoadFromSender => AddSenderRuleAsync(new SenderRuleRequest(RemoteContentSenderPolicy.Block, RemoteContentRuleKind.Address)),
        PrivacyMenuAction.ChangeRuleToImagesOnly => ChangeRuleAsync(RemoteContentSenderPolicy.ImagesOnly),
        PrivacyMenuAction.ChangeRuleToLoadEverything => ChangeRuleAsync(RemoteContentSenderPolicy.LoadEverything),
        PrivacyMenuAction.RemoveRule => RemoveRuleAsync(),
        PrivacyMenuAction.ManageRules => OpenSenderRulesAsync(),
        _ => Task.CompletedTask
    };

    /// <summary>
    /// Loads everything for the open message until Wino closes. A message blocked by a Block rule
    /// loads only for this render, so the rule applies again the next time it opens.
    /// </summary>
    [RelayCommand]
    private Task LoadOnceAsync()
    {
        var decision = CurrentDecision;

        if (decision?.Rule is { Policy: RemoteContentSenderPolicy.Block } &&
            decision.Reason is RemoteContentDecisionReason.AddressRule or RemoteContentDecisionReason.DomainRule)
        {
            return RerenderForPrivacyAsync(RemoteContentMode.LoadEverything);
        }

        return SetSessionOverrideAsync(RemoteContentMode.LoadEverything);
    }

    [RelayCommand]
    private Task LoadTrackersOnceAsync() => LoadOnceAsync();

    [RelayCommand]
    private Task BlockOnceAsync() => SetSessionOverrideAsync(RemoteContentMode.BlockAll);

    private Task SetSessionOverrideAsync(RemoteContentMode mode)
    {
        if (GetRemoteContentSessionId() is not Guid sessionId)
            return Task.CompletedTask;

        _remoteContentSessionService.Set(sessionId, mode);
        return RerenderForPrivacyAsync();
    }

    /// <summary>
    /// Creates or replaces the rule for the sender's address or domain. The rule store's change
    /// message re-renders the reader.
    /// </summary>
    [RelayCommand]
    private async Task AddSenderRuleAsync(SenderRuleRequest? request)
    {
        var decision = CurrentDecision;

        if (request == null || decision == null)
            return;

        var pattern = request.Kind == RemoteContentRuleKind.Address
            ? decision.SenderAddress
            : GetRuleDomain(decision.SenderDomain);

        if (string.IsNullOrWhiteSpace(pattern))
            return;

        if (GetRemoteContentSessionId() is Guid sessionId)
            _remoteContentSessionService.Clear(sessionId);

        await SaveRuleAsync(new RemoteContentSenderRule
        {
            Pattern = pattern,
            Kind = request.Kind,
            Policy = request.Policy,
            Source = RemoteContentRuleSource.Reader,
            CreatedUtc = DateTime.UtcNow
        });
    }

    [RelayCommand]
    private async Task ChangeRuleAsync(RemoteContentSenderPolicy policy)
    {
        if (CurrentDecision?.Rule is not RemoteContentSenderRule rule)
            return;

        rule.Policy = policy;
        await SaveRuleAsync(rule);
    }

    [RelayCommand]
    private async Task RemoveRuleAsync()
    {
        if (CurrentDecision?.Rule is not RemoteContentSenderRule rule)
            return;

        try
        {
            await _remoteContentRuleStore.DeleteAsync(rule.Id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Removing a remote content rule from the reader failed.");
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, InfoBarMessageType.Error);
        }
    }

    private async Task SaveRuleAsync(RemoteContentSenderRule rule)
    {
        try
        {
            await _remoteContentRuleStore.UpsertAsync(rule);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving a remote content rule from the reader failed.");
            _dialogService.InfoBarMessage(Translator.RemoteContentRules_AddFailedTitle, ex.Message, InfoBarMessageType.Error);
        }
    }

    [RelayCommand]
    private Task OpenSenderRulesAsync()
    {
        _navigationService.Navigate(WinoPage.SettingsPage, WinoPage.RemoteContentRulesPage, NavigationReferenceFrame.ShellFrame);
        return Task.CompletedTask;
    }

    private async Task RerenderForPrivacyAsync(RemoteContentMode? transientMode = null)
    {
        var mimeMessageInformation = initializedMimeMessageInformation;

        if (mimeMessageInformation == null)
            return;

        var loadVersion = _loadVersion;

        try
        {
            await RenderAsync(mimeMessageInformation, transientMode);
        }
        catch (Exception ex)
        {
            if (loadVersion != _loadVersion)
                return;

            Log.Error(ex, "Re-rendering the message after a privacy change failed.");
            ShowMailRenderingFailedMessage(ex);
        }
    }

    public void Receive(RemoteContentRulesChangedMessage message)
    {
        var decision = CurrentDecision;

        if (decision == null || initializedMimeMessageInformation == null)
            return;

        if (message.Pattern != null && !IsSenderAffected(decision, message.Pattern))
            return;

        _ = RerenderForPrivacyAsync();
    }

    private static bool IsSenderAffected(RemoteContentDecision decision, string pattern)
        => string.Equals(decision.Rule?.Pattern, pattern, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(decision.SenderAddress, pattern, StringComparison.OrdinalIgnoreCase) ||
           RemoteContentRulePattern.GetDomainCandidates(decision.SenderDomain).Contains(pattern, StringComparer.OrdinalIgnoreCase);
}

/// <summary>A sender rule the reader creates for the open message's sender address or domain.</summary>
public sealed record SenderRuleRequest(RemoteContentSenderPolicy Policy, RemoteContentRuleKind Kind);
