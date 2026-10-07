using System.ComponentModel;
using System.Windows.Input;
using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Per-mailbox Wino Intelligence (Windows WinoIntelligenceManagementPage): the hero with state, counts
/// and quota, status bars, the enable switch, what gets indexed (folders, new mail, plan or progress),
/// the intelligence features and the local data card. The window title follows the mailbox address.
/// </summary>
public sealed class WinoIntelligenceManagementPageViewController(WinoIntelligenceManagementPageViewModel viewModel,
    IIntelligenceCoverageHandoff coverageHandoff, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<WinoIntelligenceManagementPageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private readonly NSStackView _folderRows = WinoLayout.VStack(0);
    private readonly NSStackView _indicatorRows = WinoLayout.VStack(0);
    private BindingScope? _folderScope;
    private BindingScope? _indicatorScope;
    private bool _applying;

    public string? PageTitle => string.IsNullOrWhiteSpace(ViewModel.AccountAddress) ? Translator.SemanticIndex_Title : ViewModel.AccountAddress;
    public event EventHandler? PageTitleChanged;

    private void Watch<TValue>(Func<WinoIntelligenceManagementPageViewModel, TValue> read, Action<TValue> apply)
        => Bindings.Own(new AnyPropertyBinding<WinoIntelligenceManagementPageViewModel, TValue>(ViewModel, read, apply, Dispatcher, ReportError));

    private void Watch<TSource, TValue>(TSource source, Func<TSource, TValue> read, Action<TValue> apply, BindingScope scope) where TSource : INotifyPropertyChanged
        => scope.Own(new AnyPropertyBinding<TSource, TValue>(source, read, apply, Dispatcher, ReportError));

    protected override void BuildPage()
    {
        var vm = ViewModel;
        Bind.Bind(vm, nameof(vm.AccountAddress), s => s.AccountAddress, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));

        Add(Hero());
        Add(Spacer(4));

        var spinner = IntelligenceViews.Spinner();
        var updating = Row(spinner, WinoStyle.Label(Translator.WinoIntelligence_Updating, WinoStyle.Body, WinoStyle.PrimaryText));
        // Only a slow network refresh shows the row, so a quick one does not shift the cards.
        var showUpdating = IntelligenceViews.DelayedProgressRow(updating, spinner, () => !Bindings.IsDisposed);
        Watch(s => s.IsRemoteRefreshInProgress, showUpdating);
        Add(updating);
        Add(Bar(s => s.HasPurchaseStatus ? s.PurchaseStatusMessage : null, WinoInfoBarSeverity.Informational, Translator.WinoAccount_Management_RefreshPurchases, vm.RetryRemoteRefreshCommand));
        // Windows: only the remote-refresh error bar is IsClosable="True"; the others are not closable.
        Add(Bar(s => s.HasRemoteRefreshError ? s.RemoteRefreshError : null, WinoInfoBarSeverity.Warning, Translator.Buttons_Retry, vm.RetryRemoteRefreshCommand, closable: true));
        Add(StatusBar());
        Add(Bar(s => s.ShouldShowEverythingWarning ? Translator.SemanticIndex_EverythingWarning : null, WinoInfoBarSeverity.Warning));

        Add(EnableCard());
        Add(CoverageGroup());
        Add(FeaturesGroup());

        var wipe = Bind.Button(Translator.SemanticIndex_WipeData, vm.WipeIntelligenceDataCommand);
        wipe.HasDestructiveAction = true;
        wipe.ContentTintColor = WinoStyle.Critical;
        var data = Card(Translator.SemanticIndex_DataTitle, Translator.SemanticIndex_DataDescription, WinoIconGlyph.LockClosed, wipe);
        Watch(s => s.IsIndexedDataGroupVisible, on => data.Hidden = !on);
        Add(Spacer(8));
        Add(data);

        Bind.Collection(vm.IntelligenceFolderCoverageItems, RebuildFolders);
        Bind.Collection(vm.IntelligenceIndicatorSettings, RebuildIndicators);
#if DEBUG
        WinoIntelligenceDebug.Management = vm;
#endif
    }

    /// <summary>
    /// Windows keeps this page cached so the coverage editor can hand its result back on Back. The Mac
    /// Settings window builds a new page, so on Back the page loads first and then applies the handoff.
    /// </summary>
    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        if (mode != NavigationMode.Back || !coverageHandoff.TryTake(out var result) || result is null)
        {
            await ViewModel.InitializeAsync(mode, parameter!);
            return;
        }
        await ViewModel.InitializeAsync(NavigationMode.New, parameter!);
        coverageHandoff.Publish(result);
        await ViewModel.InitializeAsync(NavigationMode.Back, parameter!);
    }

    private static NSView Spacer(double height)
    {
        var view = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        view.HeightAnchor.ConstraintEqualTo((nfloat)height).Active = true;
        return view;
    }

    private WinoInfoBar Bar(Func<WinoIntelligenceManagementPageViewModel, string?> message, WinoInfoBarSeverity severity, string? action = null, ICommand? command = null, bool closable = false)
    {
        var bar = InfoBar(severity, null, null);
        bar.IsClosable = closable;
        Watch(message, text => { bar.Message = text; bar.Hidden = string.IsNullOrWhiteSpace(text); });
        if (action is not null && command is not null)
        {
            bar.ActionTitle = action;
            bar.ActionInvoked += (_, _) => { if (command.CanExecute(null)) command.Execute(null); };
        }
        return bar;
    }

    private WinoInfoBar StatusBar()
    {
        var vm = ViewModel;
        var bar = InfoBar(WinoInfoBarSeverity.Informational, null, null);
        Watch(s => (s.IsStatusInfoBarVisible, s.StatusInfoBarTitle, s.StatusInfoBarMessage, s.StatusInfoBarType, s.IsConsentActionVisible), state =>
        {
            bar.Hidden = !state.IsStatusInfoBarVisible;
            bar.Title = state.StatusInfoBarTitle;
            bar.Message = state.StatusInfoBarMessage;
            bar.Severity = Severity(state.StatusInfoBarType);
            bar.ActionTitle = state.IsConsentActionVisible ? Translator.Intelligence_ReviewPrivacyPolicyButton : null;
        });
        bar.ActionInvoked += (_, _) => vm.OpenIntelligenceSettingsCommand.Execute(null);
        return bar;
    }

    private static WinoInfoBarSeverity Severity(InfoBarMessageType type) => type switch
    {
        InfoBarMessageType.Success => WinoInfoBarSeverity.Success,
        InfoBarMessageType.Warning => WinoInfoBarSeverity.Warning,
        InfoBarMessageType.Error => WinoInfoBarSeverity.Error,
        _ => WinoInfoBarSeverity.Informational
    };

    private static NSColor StateColor(InfoBarMessageType type) => type switch
    {
        InfoBarMessageType.Success => WinoStyle.Dynamic(WinoStyle.Hex(0x2F9E63), WinoStyle.Hex(0x6CCB5F)),
        InfoBarMessageType.Warning => WinoStyle.Caution,
        InfoBarMessageType.Error => WinoStyle.Critical,
        _ => WinoStyle.Accent
    };

    /// <summary>Hero: brand tile, title and address, the state pill; counts, coverage pill and quota.</summary>
    private NSView Hero()
    {
        var tile = new WinoSurfaceView { Fill = WinoSettingsStyle.SubtleFill, Stroke = WinoSettingsStyle.CardStroke, CornerRadius = 10 };
        WinoLayout.Size(tile, 44, 44);
        var glyph = new BrandIconView(WinoIconGlyph.WinoIntelligence, 22);
        tile.AddSubview(glyph);
        NSLayoutConstraint.ActivateConstraints([glyph.CenterXAnchor.ConstraintEqualTo(tile.CenterXAnchor), glyph.CenterYAnchor.ConstraintEqualTo(tile.CenterYAnchor)]);

        var title = WinoStyle.Label(Translator.SemanticIndex_Title, NSFont.SystemFontOfSize(18, NSFontWeight.Semibold), WinoStyle.PrimaryText);
        var address = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        Watch(s => s.AccountAddress, text => address.StringValue = text ?? string.Empty);
        var titles = WinoLayout.VStack(2, title, address);
        titles.Alignment = NSLayoutAttribute.Leading;

        var dot = new WinoSurfaceView { CornerRadius = 3.5 };
        WinoLayout.Size(dot, 7, 7);
        var ring = IntelligenceViews.Spinner(13);
        var stateText = WinoStyle.Label(string.Empty, WinoStyle.CaptionStrong, WinoStyle.PrimaryText);
        var pillRow = WinoLayout.HStack(7, dot, ring, stateText);
        pillRow.EdgeInsets = new NSEdgeInsets(0, 10, 0, 10);
        var pill = new WinoSurfaceView { Fill = WinoStyle.Dynamic(NSColor.White, WinoStyle.Hex(0xFFFFFF, 0.08)), Stroke = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.08), WinoStyle.Hex(0xFFFFFF, 0.10)), CornerRadius = 12 };
        WinoLayout.Fill(pillRow, pill);
        pill.HeightAnchor.ConstraintEqualTo(24).Active = true;
        Watch(s => (s.HeroStateText, s.HeroStateType, s.IsHeroProgressVisible), state =>
        {
            stateText.StringValue = state.HeroStateText ?? string.Empty;
            dot.Fill = StateColor(state.HeroStateType);
            dot.Hidden = state.IsHeroProgressVisible;
            IntelligenceViews.SetSpinning(ring, state.IsHeroProgressVisible);
            pill.Hidden = string.IsNullOrWhiteSpace(state.HeroStateText);
        });

        var top = WinoLayout.HStack(14, tile, titles, WinoLayout.Spacer(), pill);

        var indexed = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        Watch(s => (s.IndexedMessageCountDetail, s.IsJobActive), state => { indexed.StringValue = state.IndexedMessageCountDetail ?? string.Empty; indexed.Hidden = state.IsJobActive; });
        var coverageText = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        var coverage = new WinoSurfaceView { Fill = WinoSettingsStyle.SubtleFill, CornerRadius = 10 };
        WinoLayout.Fill(coverageText, coverage, 2, 8, 2, 8);
        coverage.HeightAnchor.ConstraintEqualTo(20).Active = true;
        Watch(s => (s.TotalCoverageSummary, s.IsPageReady), state => { coverageText.StringValue = state.TotalCoverageSummary; coverage.Hidden = !state.IsPageReady; });
        var quotaText = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        var quotaBar = new WinoBarView(180);
        WinoAccessibility.Label(quotaBar, Translator.WinoIntelligence_AvailableQuota);
        Watch(s => (s.QuotaSummary, s.IsQuotaAvailable, s.QuotaUsagePercentage), state =>
        {
            quotaText.StringValue = state.QuotaSummary ?? string.Empty;
            quotaBar.Hidden = !state.IsQuotaAvailable;
            quotaBar.Value = state.QuotaUsagePercentage;
        });
        var quota = WinoLayout.VStack(5, quotaText, quotaBar);
        quota.Alignment = NSLayoutAttribute.Trailing;
        var bottom = WinoLayout.HStack(10, indexed, coverage, WinoLayout.Spacer(), quota);
        bottom.Alignment = NSLayoutAttribute.Bottom;

        var stack = WinoLayout.VStack(12, top, bottom);
        stack.Alignment = NSLayoutAttribute.Leading;
        top.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        bottom.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        return IntelligenceViews.Surface(stack, 16, 8);
    }

    /// <summary>Enable intelligence: switch with SemanticIndex On/Off, a ring while busy.</summary>
    private NSView EnableCard()
    {
        var busy = IntelligenceViews.Spinner();
        var toggle = new WinoTextSwitch(Translator.SemanticIndex_On, Translator.SemanticIndex_Off, Translator.SemanticIndex_EnableTitle);
        Watch(s => s.IsBusy, on => IntelligenceViews.SetSpinning(busy, on));
        Watch(s => s.CanChangeSemanticIndexingState, on => toggle.IsEnabled = on);
        Watch(s => s.IsSemanticIndexingEnabled, on => { if (!_applying) toggle.IsOn = on; });
        toggle.Toggled += async (_, _) =>
        {
            if (_applying || !ViewModel.IsPageReady) return;
            _applying = true;
            try { toggle.IsOn = await ViewModel.SetSemanticIndexingEnabledAsync(toggle.IsOn); }
            catch (Exception exception) { ReportError(exception); toggle.IsOn = ViewModel.IsSemanticIndexingEnabled; }
            finally { _applying = false; }
        };
        var card = Card(Translator.SemanticIndex_EnableTitle, Translator.SemanticIndex_Description, WinoIconGlyph.None, Row(busy, toggle));
        card.LeadingView = new BrandIconView(WinoIconGlyph.WinoIntelligence, 20);
        return card;
    }

    /// <summary>What gets indexed: folders, new mail, and the plan or progress card.</summary>
    private NSView CoverageGroup()
    {
        var vm = ViewModel;
        var group = new WinoSettingsGroup(Translator.SemanticIndex_CoverageGroupTitle);

        var pick = Bind.Button(Translator.SemanticIndex_FoldersPick, vm.OpenCoverageEditorCommand);
        Watch(s => s.CanEditIntelligenceFolders, on => pick.Enabled = on);
        var folders = new WinoSettingsExpander(Translator.SemanticIndex_FoldersTitle, null, WinoIconGlyph.Folder, pick);
        Watch(s => s.SelectedIntelligenceFoldersDescription, text => folders.HeaderCard.Description = text);
        Watch(s => s.HasCoverageFolders, on => folders.IsExpanded = on);
        _folderRows.Alignment = NSLayoutAttribute.Leading;
        folders.Add(_folderRows, 0, 0, 0, 0);
        group.Add(folders);

        var emptyTitle = WinoStyle.Label(Translator.SemanticIndex_CoverageEmptyTitle, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0);
        var emptyText = WinoStyle.Label(Translator.SemanticIndex_CoverageEmptyDescription, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText, 0);
        var emptyPick = Bind.Button(Translator.SemanticIndex_FoldersPick, vm.OpenCoverageEditorCommand);
        var emptyStack = WinoLayout.VStack(6, emptyTitle, emptyText, emptyPick);
        emptyStack.Alignment = NSLayoutAttribute.CenterX;
        var empty = IntelligenceViews.Surface(emptyStack, 20);
        Watch(s => s.IsCoverageEmptyStateVisible, on => empty.Hidden = !on);
        group.Add(empty);

        var segmented = Bind.Segmented(vm, [Translator.SemanticIndex_AutomaticNewMessages, Translator.SemanticIndex_ManualNewMessages],
            nameof(vm.NewMessageModeIndex), s => s.NewMessageModeIndex, (s, v) => s.NewMessageModeIndex = v);
        group.Add(Card(Translator.SemanticIndex_CoverageNewMailTitle, Translator.SemanticIndex_CoverageNewMailDescription, WinoIconGlyph.Mail, segmented));

        var start = Bind.Button(string.Empty, vm.StartIndexingCommand, primary: true);
        start.KeyEquivalent = string.Empty;
        Watch(s => s.StartButtonText, text => start.Title = text ?? string.Empty);
        var plan = Card(string.Empty, null, WinoIconGlyph.None, start);
        plan.LeadingView = new BrandIconView(WinoIconGlyph.Play, 20);
        Watch(s => (s.PlanCardTitle, s.PlanCardDescription, s.IsJobActive), state =>
        {
            plan.Header = state.PlanCardTitle;
            plan.Description = state.PlanCardDescription;
            plan.Hidden = state.IsJobActive;
        });
        group.Add(plan);

        var bar = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Bar, Indeterminate = true, TranslatesAutoresizingMaskIntoConstraints = false };
        var cancel = Bind.Button(Translator.SemanticIndex_CancelIndexing, vm.CancelIndexingCommand);
        var progress = Card(Translator.SemanticIndex_ProgressTitle, null, WinoIconGlyph.None, cancel);
        progress.LeadingView = new BrandIconView(WinoIconGlyph.WinoIntelligence, 20);
        progress.BottomContent = bar;
        Watch(s => (s.ProgressSummary, s.IsJobActive), state =>
        {
            progress.Description = state.ProgressSummary;
            progress.Hidden = !state.IsJobActive;
            if (state.IsJobActive) bar.StartAnimation(null); else bar.StopAnimation(null);
        });
        group.Add(progress);

        Watch(s => s.IsPageReady, on => group.Hidden = !on);
        Watch(s => s.IsCoverageEditable, on => { SettingsBinder.SetEnabled(folders, on); SettingsBinder.SetEnabled(segmented, on); });
        return group;
    }

    /// <summary>Intelligence features: the Daily Briefing switch and the indicator visibility list.</summary>
    private NSView FeaturesGroup()
    {
        var group = new WinoSettingsGroup(Translator.SemanticIndex_CoverageFeaturesGroupTitle);
        var briefing = new WinoTextSwitch(Translator.SemanticIndex_On, Translator.SemanticIndex_Off, Translator.SemanticIndex_DailyBriefingTitle);
        Watch(s => s.IsDailyBriefingEnabled, on => briefing.IsOn = on);
        Watch(s => s.CanChangeIntelligencePreferences, on => briefing.IsEnabled = on);
        briefing.Toggled += async (_, _) =>
        {
            if (!ViewModel.IsPageReady) return;
            try { briefing.IsOn = await ViewModel.SetDailyBriefingEnabledAsync(briefing.IsOn); }
            catch (Exception exception) { ReportError(exception); briefing.IsOn = ViewModel.IsDailyBriefingEnabled; }
        };
        group.Add(Card(Translator.SemanticIndex_DailyBriefingTitle, Translator.SemanticIndex_DailyBriefingDescription, WinoIconGlyph.DailyBriefing, briefing));

        var indicators = new WinoSettingsExpander(Translator.SemanticIndex_IntelligenceIndicatorsTitle, Translator.SemanticIndex_IntelligenceIndicatorsDescription, WinoIconGlyph.NoteEdit);
        _indicatorRows.Alignment = NSLayoutAttribute.Leading;
        indicators.Add(_indicatorRows, 0, 0, 0, 0);
        Watch(s => s.CanChangeIntelligencePreferences, on => SettingsBinder.SetEnabled(_indicatorRows, on));
        group.Add(indicators);

        Watch(s => s.IsFeaturesGroupVisible, on => group.Hidden = !on);
        return group;
    }

    private void RebuildFolders()
    {
        _folderScope?.Dispose();
        _folderScope = Bindings.Own(new BindingScope());
        var scope = _folderScope;
        Clear(_folderRows);
        foreach (var item in ViewModel.IntelligenceFolderCoverageItems.ToList())
        {
            var covered = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
            var selected = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong, WinoStyle.PrimaryText);
            Watch(item, i => (i.CoveredSummary, i.SelectedCountSummary), s => { covered.StringValue = s.CoveredSummary; selected.StringValue = s.SelectedCountSummary; }, scope);
            var remove = SettingsBinder.CreateButton(string.Empty, icon: WinoIconGlyph.Dismiss);
            remove.Bordered = false;
            WinoAccessibility.Label(remove, Translator.Buttons_Delete);
            var command = ViewModel.RemoveCoverageFolderCommand;
            remove.Activated += (_, _) => { if (command.CanExecute(item)) command.Execute(item); };
            var card = new WinoSettingsCard(item.DisplayName, item.AvailableSummary, WinoIconGlyph.Folder, Row(covered, selected, remove)) { IsNested = true };
            Watch(item, i => i.AvailableSummary, text => card.Description = text, scope);
            AddRow(_folderRows, card);
        }
    }

    private void RebuildIndicators()
    {
        _indicatorScope?.Dispose();
        _indicatorScope = Bindings.Own(new BindingScope());
        var scope = _indicatorScope;
        Clear(_indicatorRows);
        foreach (var item in ViewModel.IntelligenceIndicatorSettings.ToList())
        {
            var toggle = new WinoTextSwitch(Translator.SemanticIndex_On, Translator.SemanticIndex_Off, item.DisplayName);
            Watch(item, i => (i.IsVisible, i.IsBusy), s => { toggle.IsOn = s.IsVisible; toggle.IsEnabled = !s.IsBusy && ViewModel.CanChangeIntelligencePreferences; }, scope);
            toggle.Toggled += async (_, _) =>
            {
                if (!ViewModel.IsPageReady || item.IsBusy || toggle.IsOn == item.IsVisible) return;
                item.IsBusy = true;
                try { item.IsVisible = await ViewModel.SetIntelligenceIndicatorVisibilityAsync(item.Identifier, toggle.IsOn); }
                catch (Exception exception) { ReportError(exception); }
                finally { item.IsBusy = false; toggle.IsOn = item.IsVisible; }
            };
            var card = new WinoSettingsCard(item.DisplayName, null, WinoIconGlyph.None, toggle) { IsNested = true };
            AddRow(_indicatorRows, card);
        }
    }

    private static void Clear(NSStackView stack)
    {
        foreach (var view in stack.ArrangedSubviews) { stack.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
    }

    private static void AddRow(NSStackView stack, NSView row)
    {
        if (stack.ArrangedSubviews.Length > 0)
        {
            var separator = new WinoSeparator { Fill = WinoSettingsStyle.CardStroke };
            stack.AddArrangedSubview(separator);
            separator.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        }
        row.TranslatesAutoresizingMaskIntoConstraints = false;
        stack.AddArrangedSubview(row);
        row.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
    }
}
