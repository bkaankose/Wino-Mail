using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Bindings;

namespace Wino.Mail.Linux.Views.Settings.Intelligence;

/// <summary>
/// Per-mailbox Wino Intelligence (Windows WinoIntelligenceManagementPage, macOS WinoIntelligenceManagementPageViewController):
/// the hero with state, counts and quota, the status bars, the enable switch, what gets indexed (folders, new mail, plan or
/// progress), the intelligence features and the local data. The page title follows the mailbox address. Most rows read
/// derived ViewModel state, so they bind to any property change.
/// </summary>
internal sealed partial class WinoIntelligenceManagementPage(WinoIntelligenceManagementPageViewModel viewModel, WinoViewServices services,
    IIntelligenceCoverageHandoff coverageHandoff)
    : SettingsPageView<WinoIntelligenceManagementPageViewModel>(viewModel, services), ISettingsPageTitleSource
{
    private Adw.ExpanderRow? _folders;
    private Adw.ExpanderRow? _indicators;
    private readonly List<Gtk.Widget> _folderRows = [];
    private readonly List<Gtk.Widget> _indicatorRows = [];
    private GtkBinder<WinoIntelligenceManagementPageViewModel>? _folderBinder;
    private GtkBinder<WinoIntelligenceManagementPageViewModel>? _indicatorBinder;
    private bool _applying;

    public string? PageTitle => string.IsNullOrWhiteSpace(ViewModel.AccountAddress) ? Translator.SemanticIndex_Title : ViewModel.AccountAddress;

    public event EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        Bind.Bind(nameof(vm.AccountAddress), static s => s.AccountAddress, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));

        var top = Page.AddGroup();
        top.HidesWhenEmpty = false;
        var stack = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        stack.Append(Hero());
        var updating = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        updating.Append(Adw.Spinner.New());
        updating.Append(Gtk.Label.New(Translator.WinoIntelligence_Updating));
        Bind.BindAny(static s => s.IsRemoteRefreshInProgress, updating.SetVisible);
        stack.Append(updating);
        stack.Append(Bar(static s => s.HasPurchaseStatus ? s.PurchaseStatusMessage : null, WinoInfoBarSeverity.Informational,
            Translator.WinoAccount_Management_RefreshPurchases, vm.RetryRemoteRefreshCommand).Root);
        // Windows: only the remote-refresh error bar is closable.
        stack.Append(Bar(static s => s.HasRemoteRefreshError ? s.RemoteRefreshError : null, WinoInfoBarSeverity.Warning,
            Translator.Buttons_Retry, vm.RetryRemoteRefreshCommand, closable: true).Root);
        stack.Append(StatusBar().Root);
        stack.Append(Bar(static s => s.ShouldShowEverythingWarning ? Translator.SemanticIndex_EverythingWarning : null, WinoInfoBarSeverity.Warning).Root);
        top.Root.Add(stack);

        AddGroup(null, EnableRow());
        CoverageGroup();
        FeaturesGroup();

        var wipe = WinoA11y.Rename(Settings.Button(Translator.SemanticIndex_WipeData, vm.WipeIntelligenceDataCommand, destructive: true), Translator.SemanticIndex_WipeData);
        wipe.SetValign(Gtk.Align.Center);
        var dataRow = WinoSettingsRows.Action(Translator.SemanticIndex_DataTitle, Translator.SemanticIndex_DataDescription, WinoIconGlyph.LockClosed);
        WinoSettingsRows.Suffix(dataRow, wipe);
        var data = AddGroup(Translator.SemanticIndex_CoverageIndexedDataGroupTitle, dataRow);
        Bind.BindAny(static s => s.IsIndexedDataGroupVisible, data.Root.SetVisible);

        Bind.BindCollection(vm.IntelligenceFolderCoverageItems, RebuildFolders);
        Bind.BindCollection(vm.IntelligenceIndicatorSettings, RebuildIndicators);
#if DEBUG
        Current = this;
#endif
    }

    /// <summary>
    /// Settings builds a new page on Back, as the Mac window does, so the coverage editor's result arrives through the
    /// handoff: the page loads first and then applies it (Windows keeps the page cached instead).
    /// </summary>
    protected override async Task NavigateAsync(NavigationMode mode, object? parameter)
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

    protected override Task DeactivateAsync()
    {
#if DEBUG
        if (ReferenceEquals(Current, this)) Current = null;
#endif
        return base.DeactivateAsync();
    }

    private WinoInfoBar Bar(Func<WinoIntelligenceManagementPageViewModel, string?> message, WinoInfoBarSeverity severity,
        string? action = null, System.Windows.Input.ICommand? command = null, bool closable = false)
    {
        var bar = new WinoInfoBar(severity) { IsClosable = closable };
        Settings.Own(bar);
        if (action is not null && command is not null)
        {
            bar.ActionTitle = action;
            bar.ActionInvoked += _ =>
            {
                if (command.CanExecute(null)) command.Execute(null);
            };
        }
        Bind.BindAny(message, text =>
        {
            bar.Message = text ?? string.Empty;
            bar.IsOpen = !string.IsNullOrWhiteSpace(text);
        });
        return bar;
    }

    private WinoInfoBar StatusBar()
    {
        var vm = ViewModel;
        var bar = new WinoInfoBar(WinoInfoBarSeverity.Informational);
        Settings.Own(bar);
        Bind.BindAny(static s => (s.IsStatusInfoBarVisible, s.StatusInfoBarTitle, s.StatusInfoBarMessage, s.StatusInfoBarType, s.IsConsentActionVisible), state =>
        {
            bar.Severity = IntelligenceViews.Severity(state.StatusInfoBarType);
            bar.Title = state.StatusInfoBarTitle;
            bar.Message = state.StatusInfoBarMessage;
            bar.ActionTitle = state.IsConsentActionVisible ? Translator.Intelligence_ReviewPrivacyPolicyButton : null;
            bar.IsOpen = state.IsStatusInfoBarVisible;
        });
        bar.ActionInvoked += _ =>
        {
            if (vm.OpenIntelligenceSettingsCommand.CanExecute(null)) vm.OpenIntelligenceSettingsCommand.Execute(null);
        };
        return bar;
    }

    /// <summary>Hero: brand tile, title and address, the state pill; counts, the coverage chip and the quota.</summary>
    private Gtk.Widget Hero()
    {
        var title = IntelligenceViews.Text(Translator.SemanticIndex_Title, "title-4");
        var address = IntelligenceViews.Text(null, "caption", "dim-label");
        Bind.BindAny(static s => s.AccountAddress, text =>
        {
            address.SetText(text ?? string.Empty);
            address.SetVisible(!string.IsNullOrWhiteSpace(text));
        });
        var titles = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        titles.SetValign(Gtk.Align.Center);
        titles.SetHexpand(true);
        titles.Append(title);
        titles.Append(address);

        var dot = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        dot.AddCssClass("wino-intelligence-dot");
        dot.SetSizeRequest(8, 8);
        dot.SetValign(Gtk.Align.Center);
        var ring = Adw.Spinner.New();
        ring.SetSizeRequest(14, 14);
        var stateText = Gtk.Label.New(string.Empty);
        stateText.AddCssClass("caption-heading");
        var pill = Gtk.Box.New(Gtk.Orientation.Horizontal, 7);
        pill.AddCssClass("card");
        pill.AddCssClass("wino-intelligence-pill");
        pill.SetValign(Gtk.Align.Center);
        foreach (var part in (Gtk.Widget[])[dot, ring, stateText]) pill.Append(part);
        dot.SetMarginStart(10);
        stateText.SetMarginEnd(10);
        Bind.BindAny(static s => (s.HeroStateText, s.HeroStateType, s.IsHeroProgressVisible), state =>
        {
            stateText.SetText(state.HeroStateText ?? string.Empty);
            foreach (var tone in (string[])["success", "warning", "error", "accent"]) dot.RemoveCssClass("wino-dot-" + tone);
            dot.AddCssClass("wino-dot-" + state.HeroStateType switch
            {
                InfoBarMessageType.Success => "success",
                InfoBarMessageType.Warning => "warning",
                InfoBarMessageType.Error => "error",
                _ => "accent"
            });
            dot.SetVisible(!state.IsHeroProgressVisible);
            ring.SetVisible(state.IsHeroProgressVisible);
            if (state.IsHeroProgressVisible) ring.SetMarginStart(10);
            pill.SetVisible(!string.IsNullOrWhiteSpace(state.HeroStateText));
            WinoA11y.Label(pill, state.HeroStateText);
        });
        var top = Gtk.Box.New(Gtk.Orientation.Horizontal, 14);
        top.Append(IntelligenceViews.BrandTile());
        top.Append(titles);
        top.Append(pill);

        var indexed = IntelligenceViews.Text(null, "caption", "dim-label");
        indexed.SetEllipsize(Pango.EllipsizeMode.End);
        indexed.SetWrap(false);
        Bind.BindAny(static s => (s.IndexedMessageCountDetail, s.IsJobActive), state =>
        {
            indexed.SetText(state.IndexedMessageCountDetail ?? string.Empty);
            indexed.SetVisible(!state.IsJobActive);
        });
        var coverage = Gtk.Label.New(string.Empty);
        coverage.AddCssClass("caption");
        coverage.AddCssClass("wino-intelligence-chip");
        Bind.BindAny(static s => (s.TotalCoverageSummary, s.IsPageReady), state =>
        {
            coverage.SetText(state.TotalCoverageSummary);
            coverage.SetVisible(state.IsPageReady);
        });
        var quotaText = IntelligenceViews.Text(null, "caption", "dim-label");
        quotaText.SetXalign(1);
        quotaText.SetWrap(false);
        quotaText.SetEllipsize(Pango.EllipsizeMode.End);
        var quotaBar = IntelligenceViews.Bar(0, false, 180);
        WinoA11y.Label(quotaBar, Translator.WinoIntelligence_AvailableQuota);
        Bind.BindAny(static s => (s.QuotaSummary, s.IsQuotaAvailable, s.QuotaUsagePercentage), state =>
        {
            quotaText.SetText(state.QuotaSummary ?? string.Empty);
            quotaText.SetTooltipText(state.QuotaSummary);
            quotaBar.SetVisible(state.IsQuotaAvailable);
            IntelligenceViews.SetBar(quotaBar, state.QuotaUsagePercentage, false);
        });
        var quota = Gtk.Box.New(Gtk.Orientation.Vertical, 5);
        quota.SetHexpand(true);
        quota.SetHalign(Gtk.Align.End);
        quota.Append(quotaText);
        quota.Append(quotaBar);
        var bottom = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        indexed.SetValign(Gtk.Align.End);
        coverage.SetValign(Gtk.Align.End);
        bottom.Append(indexed);
        bottom.Append(coverage);
        bottom.Append(quota);

        var card = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        card.AddCssClass("card");
        foreach (var part in (Gtk.Widget[])[top, bottom])
        {
            part.SetMarginStart(16);
            part.SetMarginEnd(16);
        }
        top.SetMarginTop(16);
        bottom.SetMarginBottom(16);
        card.Append(top);
        card.Append(bottom);
        return card;
    }

    /// <summary>Enable intelligence: the switch shows the ViewModel's answer again when the call refuses.</summary>
    private Adw.ActionRow EnableRow()
    {
        var row = WinoSettingsRows.Action(Translator.SemanticIndex_EnableTitle, Translator.SemanticIndex_Description);
        row.AddPrefix(new WinoIcon(WinoIconGlyph.WinoIntelligence, 20, WinoIconMode.Colorful).Root);
        var busy = Adw.Spinner.New();
        busy.SetValign(Gtk.Align.Center);
        var toggle = Gtk.Switch.New();
        toggle.SetValign(Gtk.Align.Center);
        WinoA11y.Label(toggle, Translator.SemanticIndex_EnableTitle);
        Bind.BindAny(static s => s.IsBusy, busy.SetVisible);
        Bind.BindAny(static s => s.CanChangeSemanticIndexingState, toggle.SetSensitive);
        Bind.BindAny(static s => s.IsSemanticIndexingEnabled, on => SetQuietly(toggle, on));
        Settings.Own(GtkSignals.OnNotify(toggle, "active", () =>
        {
            if (_applying || !ViewModel.IsPageReady || toggle.GetActive() == ViewModel.IsSemanticIndexingEnabled) return;
            Observe(ApplySwitchAsync(toggle, ViewModel.SetSemanticIndexingEnabledAsync, () => ViewModel.IsSemanticIndexingEnabled));
        }));
        WinoSettingsRows.Suffix(row, busy);
        WinoSettingsRows.Suffix(row, toggle);
        return row;
    }

    private async Task ApplySwitchAsync(Gtk.Switch toggle, Func<bool, Task<bool>> apply, Func<bool> current)
    {
        _applying = true;
        try
        {
            var result = await apply(toggle.GetActive());
            RunOnUI(() => SetQuietly(toggle, result));
        }
        catch (Exception exception)
        {
            ReportError(exception);
            RunOnUI(() => SetQuietly(toggle, current()));
        }
        finally { _applying = false; }
    }

    private void SetQuietly(Gtk.Switch toggle, bool on)
    {
        if (toggle.GetActive() == on) return;
        var applying = _applying;
        _applying = true;
        try { toggle.SetActive(on); }
        finally { _applying = applying; }
    }

    /// <summary>What gets indexed: folders, new mail, and the plan or the progress.</summary>
    private void CoverageGroup()
    {
        var vm = ViewModel;
        var group = Page.AddGroup(Translator.SemanticIndex_CoverageGroupTitle);
        group.HidesWhenEmpty = false;

        var pick = WinoA11y.Rename(Settings.Button(Translator.SemanticIndex_FoldersPick, vm.OpenCoverageEditorCommand), Translator.SemanticIndex_FoldersPick);
        pick.SetValign(Gtk.Align.Center);
        Bind.BindAny(static s => s.CanEditIntelligenceFolders, pick.SetSensitive);
        var folders = _folders = WinoSettingsRows.Expander(Translator.SemanticIndex_FoldersTitle, null, WinoIconGlyph.Folder);
        WinoSettingsRows.Suffix(folders, pick);
        Bind.BindAny(static s => s.SelectedIntelligenceFoldersDescription, text => folders.SetSubtitle(text ?? string.Empty));
        Bind.BindAny(static s => s.HasCoverageFolders, on =>
        {
            folders.SetEnableExpansion(on);
            folders.SetExpanded(on);
        });
        group.Add(folders);

        var emptyPick = WinoA11y.Rename(Settings.Button(Translator.SemanticIndex_FoldersPick, vm.OpenCoverageEditorCommand, suggested: true), Translator.SemanticIndex_FoldersPick);
        emptyPick.SetValign(Gtk.Align.Center);
        var empty = WinoSettingsRows.Action(Translator.SemanticIndex_CoverageEmptyTitle, Translator.SemanticIndex_CoverageEmptyDescription, WinoIconGlyph.Folder);
        WinoSettingsRows.Suffix(empty, emptyPick);
        Bind.BindAny(static s => s.IsCoverageEmptyStateVisible, empty.SetVisible);
        group.Add(empty);

        var mode = Settings.ChoiceSuffix(vm, Translator.SemanticIndex_CoverageNewMailTitle,
            [Translator.SemanticIndex_AutomaticNewMessages, Translator.SemanticIndex_ManualNewMessages],
            nameof(vm.NewMessageModeIndex), static s => s.NewMessageModeIndex, static (s, v) => s.NewMessageModeIndex = v);
        var newMail = WinoSettingsRows.Action(Translator.SemanticIndex_CoverageNewMailTitle, Translator.SemanticIndex_CoverageNewMailDescription, WinoIconGlyph.Mail);
        WinoSettingsRows.Suffix(newMail, mode);
        group.Add(newMail);
        Bind.BindAny(static s => s.IsCoverageEditable, on =>
        {
            folders.SetSensitive(on);
            newMail.SetSensitive(on);
        });

        var start = Settings.Button(string.Empty, vm.StartIndexingCommand, suggested: true);
        start.SetValign(Gtk.Align.Center);
        Bind.BindAny(static s => s.StartButtonText, text =>
        {
            start.SetLabel(text ?? string.Empty);
            WinoA11y.Rename(start, text ?? string.Empty);
        });
        var plan = WinoSettingsRows.Action(string.Empty, null, WinoIconGlyph.Play);
        WinoSettingsRows.Suffix(plan, start);
        Bind.BindAny(static s => (s.PlanCardTitle, s.PlanCardDescription, s.IsJobActive), state =>
        {
            plan.SetTitle(state.PlanCardTitle ?? string.Empty);
            plan.SetSubtitle(state.PlanCardDescription ?? string.Empty);
            plan.SetVisible(!state.IsJobActive);
        });
        group.Add(plan);

        var cancel = WinoA11y.Rename(Settings.Button(Translator.SemanticIndex_CancelIndexing, vm.CancelIndexingCommand), Translator.SemanticIndex_CancelIndexing);
        cancel.SetValign(Gtk.Align.Center);
        var progress = WinoSettingsRows.Action(Translator.SemanticIndex_ProgressTitle);
        progress.AddPrefix(new WinoIcon(WinoIconGlyph.WinoIntelligence, 20, WinoIconMode.Colorful).Root);
        var spinner = Adw.Spinner.New();
        spinner.SetValign(Gtk.Align.Center);
        WinoSettingsRows.Suffix(progress, spinner);
        WinoSettingsRows.Suffix(progress, cancel);
        Bind.BindAny(static s => (s.ProgressSummary, s.IsJobActive), state =>
        {
            progress.SetSubtitle(state.ProgressSummary ?? string.Empty);
            progress.SetVisible(state.IsJobActive);
        });
        group.Add(progress);
        Bind.BindAny(static s => s.IsPageReady, group.Root.SetVisible);
    }

    /// <summary>Intelligence features: the indicator switches and the daily briefing switch.</summary>
    private void FeaturesGroup()
    {
        var group = Page.AddGroup(Translator.SemanticIndex_CoverageFeaturesGroupTitle);
        group.HidesWhenEmpty = false;
        var indicators = _indicators = WinoSettingsRows.Expander(Translator.SemanticIndex_IntelligenceIndicatorsTitle,
            Translator.SemanticIndex_IntelligenceIndicatorsDescription, WinoIconGlyph.NoteEdit);
        group.Add(indicators);

        var briefing = Gtk.Switch.New();
        briefing.SetValign(Gtk.Align.Center);
        WinoA11y.Label(briefing, Translator.SemanticIndex_DailyBriefingTitle);
        Bind.BindAny(static s => s.IsDailyBriefingEnabled, on => SetQuietly(briefing, on));
        Bind.BindAny(static s => s.CanChangeIntelligencePreferences, on =>
        {
            briefing.SetSensitive(on);
            indicators.SetSensitive(on);
        });
        Settings.Own(GtkSignals.OnNotify(briefing, "active", () =>
        {
            if (_applying || !ViewModel.IsPageReady || briefing.GetActive() == ViewModel.IsDailyBriefingEnabled) return;
            Observe(ApplySwitchAsync(briefing, ViewModel.SetDailyBriefingEnabledAsync, () => ViewModel.IsDailyBriefingEnabled));
        }));
        var briefingRow = WinoSettingsRows.Action(Translator.SemanticIndex_DailyBriefingTitle, Translator.SemanticIndex_DailyBriefingDescription, WinoIconGlyph.DailyBriefing);
        WinoSettingsRows.Suffix(briefingRow, briefing);
        group.Add(briefingRow);
        Bind.BindAny(static s => s.IsFeaturesGroupVisible, group.Root.SetVisible);
    }

    private void RebuildFolders()
    {
        if (_folders is not { } expander) return;
        _folderBinder?.Scope.Dispose();
        var binder = _folderBinder = Bind.Child();
        foreach (var row in _folderRows) expander.Remove(row);
        _folderRows.Clear();
        foreach (var item in ViewModel.IntelligenceFolderCoverageItems.ToList())
        {
            var row = WinoSettingsRows.Action(item.DisplayName, item.AvailableSummary, WinoIconGlyph.Folder);
            var covered = IntelligenceViews.Text(null, "caption", "dim-label");
            covered.SetWrap(false);
            var selected = Gtk.Label.New(string.Empty);
            selected.AddCssClass("heading");
            var itemBinder = binder.For(item);
            itemBinder.BindAny(static i => (i.CoveredSummary, i.SelectedCountSummary, i.AvailableSummary), state =>
            {
                covered.SetText(state.CoveredSummary);
                selected.SetText(state.SelectedCountSummary);
                row.SetSubtitle(state.AvailableSummary);
            });
            var remove = WinoSettingsRows.GlyphButton(WinoIconGlyph.Dismiss, $"{Translator.Buttons_Delete} {item.DisplayName}");
            var command = ViewModel.RemoveCoverageFolderCommand;
            binder.Scope.Own(GtkSignals.OnClicked(remove, () =>
            {
                if (command.CanExecute(item)) command.Execute(item);
            }));
            foreach (var part in (Gtk.Widget[])[covered, selected])
            {
                part.SetValign(Gtk.Align.Center);
                WinoSettingsRows.Suffix(row, part);
            }
            WinoSettingsRows.Suffix(row, remove);
            expander.AddRow(row);
            _folderRows.Add(row);
        }
    }

    private void RebuildIndicators()
    {
        if (_indicators is not { } expander) return;
        _indicatorBinder?.Scope.Dispose();
        var binder = _indicatorBinder = Bind.Child();
        foreach (var row in _indicatorRows) expander.Remove(row);
        _indicatorRows.Clear();
        foreach (var item in ViewModel.IntelligenceIndicatorSettings.ToList())
        {
            var row = WinoSettingsRows.Action(item.DisplayName);
            var toggle = Gtk.Switch.New();
            toggle.SetValign(Gtk.Align.Center);
            WinoA11y.Label(toggle, item.DisplayName);
            var applying = false;
            binder.For(item).BindAny(static i => (i.IsVisible, i.IsBusy), state =>
            {
                applying = true;
                try { toggle.SetActive(state.IsVisible); }
                finally { applying = false; }
                toggle.SetSensitive(!state.IsBusy && ViewModel.CanChangeIntelligencePreferences);
            });
            binder.Scope.Own(GtkSignals.OnNotify(toggle, "active", () =>
            {
                if (applying || !ViewModel.IsPageReady || item.IsBusy || toggle.GetActive() == item.IsVisible) return;
                Observe(ToggleIndicatorAsync(item, toggle.GetActive()));
            }));
            WinoSettingsRows.Suffix(row, toggle);
            expander.AddRow(row);
            _indicatorRows.Add(row);
        }
    }

    private async Task ToggleIndicatorAsync(IntelligenceIndicatorSettingsItem item, bool visible)
    {
        item.IsBusy = true;
        try { item.IsVisible = await ViewModel.SetIntelligenceIndicatorVisibilityAsync(item.Identifier, visible); }
        catch (Exception exception) { ReportError(exception); }
        finally { item.IsBusy = false; }
    }

#if DEBUG
    internal static WinoIntelligenceManagementPage? Current { get; private set; }

    internal string Describe()
    {
        var vm = ViewModel;
        return $"address='{vm.AccountAddress}' ready={vm.IsPageReady} enabled={vm.IsSemanticIndexingEnabled} busy={vm.IsBusy} job={vm.IsJobActive} "
            + $"hero='{vm.HeroStateText}' quota='{vm.QuotaSummary}' folders={vm.IntelligenceFolderCoverageItems.Count} folderRows={_folderRows.Count} "
            + $"indicators={vm.IntelligenceIndicatorSettings.Count} indicatorRows={_indicatorRows.Count} features={vm.IsFeaturesGroupVisible} "
            + $"empty={vm.IsCoverageEmptyStateVisible} start='{vm.StartButtonText}' status={vm.IsStatusInfoBarVisible}:'{vm.StatusInfoBarTitle}'";
    }
#endif
}
