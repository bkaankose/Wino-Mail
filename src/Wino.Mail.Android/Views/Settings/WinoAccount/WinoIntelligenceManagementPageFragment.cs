using System.ComponentModel;
using System.Windows.Input;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.ViewModels;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;
using ActionDisposable = Wino.Mail.Android.Infrastructure.ActionDisposable;

namespace Wino.Mail.Android.Views.Settings.WinoAccount;

/// <summary>
/// Per-mailbox Wino Intelligence (Windows WinoIntelligenceManagementPage, iOS twin): the hero with state, counts and
/// quota, status bars, the enable switch, what gets indexed (folders, new mail, plan or progress), the intelligence
/// features and the local data card. The page title follows the mailbox address.
/// </summary>
/// <remarks>
/// Windows keeps this page cached so the coverage editor can hand its result back on Back. The Android Settings stack
/// keeps this page's entry (and ViewModel) under the editor but builds a new view when it is shown again, without
/// navigating the ViewModel again, so the new view applies a pending coverage result through the ViewModel's own Back
/// path.
/// </remarks>
public sealed class WinoIntelligenceManagementPageFragment : SettingsPageFragment<WinoIntelligenceManagementPageViewModel>, ISettingsPageTitleSource
{
    private BindingScope? _folderScope;
    private BindingScope? _indicatorScope;
    private bool _applying;

    public string? PageTitle => ViewModel is not { } vm || string.IsNullOrWhiteSpace(vm.AccountAddress) ? Translator.SemanticIndex_Title : vm.AccountAddress;

    private void Watch<TValue>(Func<WinoIntelligenceManagementPageViewModel, TValue> read, Action<TValue> apply)
        => Bind.Views.BindAny(VM, read, apply);

    private static void Watch<TSource, TValue>(SettingsBinder binder, TSource source, Func<TSource, TValue> read, Action<TValue> apply) where TSource : INotifyPropertyChanged
        => binder.Views.BindAny(source, read, apply);

    private static void Show(View view, bool visible) => view.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;

    protected override void BuildPage()
    {
        var vm = VM;
        _applying = false;
        Bindings.Own(new ActionDisposable(() =>
        {
            _folderScope?.Dispose();
            _indicatorScope?.Dispose();
            _folderScope = null;
            _indicatorScope = null;
        }));
        Bind.Bind(vm, nameof(vm.AccountAddress), s => s.AccountAddress, _ => UpdateTitle());

        Add(Hero(), fullWidth: true);

        var spinner = IntelligenceViews.Spinner(Ctx);
        var updating = Row(spinner, Text(Translator.WinoIntelligence_Updating));
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
        wipe.SetTextColor(WinoColors.Critical(Ctx));
        var data = StackedCard(Translator.SemanticIndex_DataTitle, Translator.SemanticIndex_DataDescription, WinoIconGlyph.LockClosed, Row(wipe));
        Watch(s => s.IsIndexedDataGroupVisible, on => Show(data, on));
        Add(data);
        if (data.LayoutParameters is LinearLayout.LayoutParams dataLayout) dataLayout.TopMargin += WinoTokens.Px(Ctx, Resource.Dimension.wino_space_s);

        ApplyPendingCoverageResult();
    }

    /// <summary>
    /// Back from the coverage editor: the result waits in the handoff. The ViewModel takes it in its Back path only when
    /// the page is loaded; otherwise the normal load already reads the saved state.
    /// </summary>
    private void ApplyPendingCoverageResult()
    {
        if (Entry is not { HasPendingWork: false } entry || !VM.IsPageReady) return;
        if (MainApplication.Services?.GetService<IIntelligenceCoverageHandoff>() is not { } handoff) return;
        if (!handoff.TryTake(out var result) || result is null) return;
        handoff.Publish(result);
        _ = ApplyAsync(VM, entry.Parameter);

        async Task ApplyAsync(WinoIntelligenceManagementPageViewModel vm, object? parameter)
        {
            try { await vm.InitializeAsync(NavigationMode.Back, parameter); }
            catch (Exception exception) { ReportError(exception); }
        }
    }

    private WinoInfoBar Bar(Func<WinoIntelligenceManagementPageViewModel, string?> message, WinoInfoBarSeverity severity, string? action = null, ICommand? command = null, bool closable = false)
    {
        var bar = InfoBar(severity, null, null);
        bar.IsClosable = closable;
        Watch(message, text =>
        {
            bar.Message = text;
            Show(bar, !string.IsNullOrWhiteSpace(text));
        });
        if (action is not null && command is not null)
        {
            bar.ActionTitle = action;
            EventHandler handler = (_, _) =>
            {
                try { if (command.CanExecute(null)) command.Execute(null); }
                catch (Exception exception) { ReportError(exception); }
            };
            bar.ActionInvoked += handler;
            Bindings.Own(new ActionDisposable(() => bar.ActionInvoked -= handler));
        }
        return bar;
    }

    private WinoInfoBar StatusBar()
    {
        var vm = VM;
        var bar = InfoBar(WinoInfoBarSeverity.Informational, null, null);
        Watch(s => (s.IsStatusInfoBarVisible, s.StatusInfoBarTitle, s.StatusInfoBarMessage, s.StatusInfoBarType, s.IsConsentActionVisible), state =>
        {
            Show(bar, state.IsStatusInfoBarVisible);
            bar.Title = state.StatusInfoBarTitle;
            bar.Message = state.StatusInfoBarMessage;
            bar.Severity = Severity(state.StatusInfoBarType);
            bar.ActionTitle = state.IsConsentActionVisible ? Translator.Intelligence_ReviewPrivacyPolicyButton : null;
        });
        EventHandler handler = (_, _) => vm.OpenIntelligenceSettingsCommand.Execute(null);
        bar.ActionInvoked += handler;
        Bindings.Own(new ActionDisposable(() => bar.ActionInvoked -= handler));
        return bar;
    }

    private static WinoInfoBarSeverity Severity(InfoBarMessageType type) => type switch
    {
        InfoBarMessageType.Success => WinoInfoBarSeverity.Success,
        InfoBarMessageType.Warning => WinoInfoBarSeverity.Warning,
        InfoBarMessageType.Error => WinoInfoBarSeverity.Error,
        _ => WinoInfoBarSeverity.Informational
    };

    private Color StateColor(InfoBarMessageType type) => type switch
    {
        InfoBarMessageType.Success => WinoTokens.Color(Ctx, Resource.Color.wino_winoaccount_state_success),
        InfoBarMessageType.Warning => WinoColors.Caution(Ctx),
        InfoBarMessageType.Error => WinoColors.Critical(Ctx),
        _ => WinoColors.Accent(Ctx)
    };

    /// <summary>Hero: brand tile, title and address, the state pill; counts, coverage pill and quota (stacked on phone width).</summary>
    private View Hero()
    {
        var context = Ctx;
        var tile = new FrameLayout(context) { Background = IntelligenceViews.Shape(context, WinoColors.SubtleFill(context), WinoColors.CardStroke(context), Resource.Dimension.wino_winoaccount_tile_radius) };
        tile.AddView(new BrandIconView(context, WinoIconGlyph.WinoIntelligence, Resource.Dimension.wino_glyph_xl), new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center));
        int tileSize = WinoTokens.Px(context, Resource.Dimension.wino_winoaccount_hero_tile);
        tile.LayoutParameters = new LinearLayout.LayoutParams(tileSize, tileSize);

        var title = Text(Translator.SemanticIndex_Title, WinoTextStyle.Title);
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) title.AccessibilityHeading = true;
        var address = Text(string.Empty, WinoTextStyle.Caption);
        address.SetSingleLine(true);
        address.Ellipsize = global::Android.Text.TextUtils.TruncateAt.Middle;
        Watch(s => s.AccountAddress, text => address.Text = text ?? string.Empty);
        var titles = Stack(Resource.Dimension.wino_space_3xs, title, address);

        var dot = new View(context);
        int dotSize = WinoTokens.Px(context, Resource.Dimension.wino_size_dot);
        dot.LayoutParameters = new LinearLayout.LayoutParams(dotSize, dotSize);
        var ring = IntelligenceViews.Spinner(context, Resource.Dimension.wino_glyph_s);
        var stateText = Text(string.Empty, WinoTextStyle.CaptionStrong);
        var pill = IntelligenceViews.HStack(context, Resource.Dimension.wino_space_xs, null, dot, ring, stateText);
        // HStack gives every child wrap-content parameters; a plain View then measures to all the space it is offered.
        dot.LayoutParameters = new LinearLayout.LayoutParams(dotSize, dotSize);
        pill.Background = IntelligenceViews.Shape(context, WinoTokens.Color(context, Resource.Color.wino_winoaccount_hero_pill_fill),
            WinoTokens.Color(context, Resource.Color.wino_winoaccount_hero_pill_stroke), Resource.Dimension.wino_radius_pill);
        WinoTokens.Pad(pill, Resource.Dimension.wino_space_sm, 0, Resource.Dimension.wino_space_sm, 0);
        pill.SetMinimumHeight(WinoTokens.Px(context, Resource.Dimension.wino_winoaccount_hero_pill_min_height));
        Watch(s => (s.HeroStateText, s.HeroStateType, s.IsHeroProgressVisible), state =>
        {
            stateText.Text = state.HeroStateText ?? string.Empty;
            dot.Background = IntelligenceViews.Oval(context, StateColor(state.HeroStateType));
            Show(dot, !state.IsHeroProgressVisible);
            IntelligenceViews.SetSpinning(ring, state.IsHeroProgressVisible);
            Show(pill, !string.IsNullOrWhiteSpace(state.HeroStateText));
        });

        var top = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        top.SetGravity(GravityFlags.CenterVertical);
        top.AddView(tile);
        top.AddView(titles, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1) { MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_space_ml) });
        // The state pill gets its own line under the title row at phone width; beside the title it squeezed the title
        // to a few letters a line.
        var pillRow = new FrameLayout(context);
        pillRow.AddView(pill, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Start | GravityFlags.CenterVertical));
        Watch(s => s.HeroStateText, text => Show(pillRow, !string.IsNullOrWhiteSpace(text)));

        var indexed = Text(string.Empty, WinoTextStyle.Caption);
        Watch(s => (s.IndexedMessageCountDetail, s.IsJobActive), state =>
        {
            indexed.Text = state.IndexedMessageCountDetail ?? string.Empty;
            Show(indexed, !state.IsJobActive);
        });
        var coverage = IntelligenceViews.Pill(context, string.Empty, WinoColors.SecondaryText(context), WinoColors.SubtleFill(context));
        WinoTokens.Pad(coverage, Resource.Dimension.wino_space_s, Resource.Dimension.wino_space_3xs, Resource.Dimension.wino_space_s, Resource.Dimension.wino_space_3xs);
        Watch(s => (s.TotalCoverageSummary, s.IsPageReady), state =>
        {
            coverage.Text = state.TotalCoverageSummary;
            Show(coverage, state.IsPageReady);
        });
        var counts = IntelligenceViews.Flow(context, [indexed, coverage], Resource.Dimension.wino_space_sm);

        var quotaText = Text(string.Empty, WinoTextStyle.Caption);
        var quotaBar = IntelligenceViews.Meter(context);
        quotaBar.ContentDescription = Translator.WinoIntelligence_AvailableQuota;
        Watch(s => (s.QuotaSummary, s.IsQuotaAvailable, s.QuotaUsagePercentage), state =>
        {
            quotaText.Text = state.QuotaSummary ?? string.Empty;
            Show(quotaBar, state.IsQuotaAvailable);
            IntelligenceViews.SetMeter(quotaBar, state.QuotaUsagePercentage);
        });
        var quota = Stack(Resource.Dimension.wino_space_2xs, quotaText, quotaBar);

        return IntelligenceViews.Surface(context, Stack(Resource.Dimension.wino_space_m, top, pillRow, counts, quota), Resource.Dimension.wino_space_l, Resource.Dimension.wino_radius_large);
    }

    /// <summary>Enable intelligence: switch with SemanticIndex On/Off, a ring while busy.</summary>
    private View EnableCard()
    {
        var busy = IntelligenceViews.Spinner(Ctx);
        var toggle = new WinoTextSwitch(Ctx, Translator.SemanticIndex_On, Translator.SemanticIndex_Off, Translator.SemanticIndex_EnableTitle);
        Watch(s => s.IsBusy, on => IntelligenceViews.SetSpinning(busy, on));
        Watch(s => s.CanChangeSemanticIndexingState, on => toggle.IsEnabled = on);
        Watch(s => s.IsSemanticIndexingEnabled, on => { if (!_applying) toggle.IsOn = on; });
        EventHandler toggled = async (_, _) =>
        {
            var vm = VM;
            if (_applying || !vm.IsPageReady) return;
            _applying = true;
            try { toggle.IsOn = await vm.SetSemanticIndexingEnabledAsync(toggle.IsOn); }
            catch (Exception exception)
            {
                ReportError(exception);
                toggle.IsOn = vm.IsSemanticIndexingEnabled;
            }
            finally { _applying = false; }
        };
        toggle.Toggled += toggled;
        Bindings.Own(new ActionDisposable(() => toggle.Toggled -= toggled));
        var card = Card(Translator.SemanticIndex_EnableTitle, Translator.SemanticIndex_Description, WinoIconGlyph.None, Row(busy, toggle));
        card.LeadingView = new BrandIconView(Ctx, WinoIconGlyph.WinoIntelligence, Resource.Dimension.wino_glyph_l);
        return card;
    }

    /// <summary>What gets indexed: folders, new mail, and the plan or progress card.</summary>
    private View CoverageGroup()
    {
        var vm = VM;
        var group = new WinoSettingsGroup(Ctx, Translator.SemanticIndex_CoverageGroupTitle);

        var pick = Bind.Button(Translator.SemanticIndex_FoldersPick, vm.OpenCoverageEditorCommand);
        Watch(s => s.CanEditIntelligenceFolders, on => pick.Enabled = on);
        var folders = new WinoSettingsExpander(Ctx, Translator.SemanticIndex_FoldersTitle, null, WinoIconGlyph.Folder, pick);
        Watch(s => s.SelectedIntelligenceFoldersDescription, text => folders.HeaderCard.Description = text);
        Watch(s => s.HasCoverageFolders, on => folders.IsExpanded = on);
        group.Add(folders);

        var emptyTitle = Text(Translator.SemanticIndex_CoverageEmptyTitle, WinoTextStyle.BodyStrong);
        emptyTitle.Gravity = GravityFlags.CenterHorizontal;
        var emptyText = Text(Translator.SemanticIndex_CoverageEmptyDescription, WinoTextStyle.Description);
        emptyText.Gravity = GravityFlags.CenterHorizontal;
        var emptyPick = Bind.Button(Translator.SemanticIndex_FoldersPick, vm.OpenCoverageEditorCommand);
        var emptyPickRow = new FrameLayout(Ctx);
        emptyPickRow.AddView(emptyPick, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.CenterHorizontal));
        var empty = IntelligenceViews.Surface(Ctx, Stack(Resource.Dimension.wino_space_xs, emptyTitle, emptyText, emptyPickRow), Resource.Dimension.wino_space_xl);
        Watch(s => s.IsCoverageEmptyStateVisible, on => Show(empty, on));
        group.Add(empty);

        var segmented = Bind.Segmented(vm, [Translator.SemanticIndex_AutomaticNewMessages, Translator.SemanticIndex_ManualNewMessages],
            nameof(vm.NewMessageModeIndex), s => s.NewMessageModeIndex, (s, v) => s.NewMessageModeIndex = v);
        group.Add(StackedCard(Translator.SemanticIndex_CoverageNewMailTitle, Translator.SemanticIndex_CoverageNewMailDescription, WinoIconGlyph.Mail, segmented));

        var start = Bind.Button(string.Empty, vm.StartIndexingCommand, primary: true);
        Watch(s => s.StartButtonText, text => start.Text = text ?? string.Empty);
        var plan = StackedCard(string.Empty, null, WinoIconGlyph.None, Row(start));
        plan.LeadingView = new BrandIconView(Ctx, WinoIconGlyph.Play, Resource.Dimension.wino_glyph_l);
        Watch(s => (s.PlanCardTitle, s.PlanCardDescription, s.IsJobActive), state =>
        {
            plan.Header = state.PlanCardTitle;
            plan.Description = state.PlanCardDescription;
            Show(plan, !state.IsJobActive);
        });
        group.Add(plan);

        // The Mac indeterminate bar becomes a spinner next to Cancel, as on iOS.
        var working = IntelligenceViews.Spinner(Ctx, Resource.Dimension.wino_glyph_l);
        var cancel = Bind.Button(Translator.SemanticIndex_CancelIndexing, vm.CancelIndexingCommand);
        var progress = StackedCard(Translator.SemanticIndex_ProgressTitle, null, WinoIconGlyph.None, Row(working, cancel));
        progress.LeadingView = new BrandIconView(Ctx, WinoIconGlyph.WinoIntelligence, Resource.Dimension.wino_glyph_l);
        Watch(s => (s.ProgressSummary, s.IsJobActive), state =>
        {
            progress.Description = state.ProgressSummary;
            Show(progress, state.IsJobActive);
            IntelligenceViews.SetSpinning(working, state.IsJobActive);
        });
        group.Add(progress);

        Watch(s => s.IsPageReady, on => Show(group, on));
        Watch(s => s.IsCoverageEditable, on =>
        {
            SettingsBinder.SetEnabled(folders, on);
            SettingsBinder.SetEnabled(segmented, on);
        });
        Bind.Collection(vm.IntelligenceFolderCoverageItems, () => RebuildFolders(folders));
        return group;
    }

    /// <summary>Intelligence features: the Daily Briefing switch and the indicator visibility list.</summary>
    private View FeaturesGroup()
    {
        var vm = VM;
        var group = new WinoSettingsGroup(Ctx, Translator.SemanticIndex_CoverageFeaturesGroupTitle);
        var briefing = new WinoTextSwitch(Ctx, Translator.SemanticIndex_On, Translator.SemanticIndex_Off, Translator.SemanticIndex_DailyBriefingTitle);
        Watch(s => s.IsDailyBriefingEnabled, on => briefing.IsOn = on);
        Watch(s => s.CanChangeIntelligencePreferences, on => briefing.IsEnabled = on);
        EventHandler toggled = async (_, _) =>
        {
            if (!vm.IsPageReady) return;
            try { briefing.IsOn = await vm.SetDailyBriefingEnabledAsync(briefing.IsOn); }
            catch (Exception exception)
            {
                ReportError(exception);
                briefing.IsOn = vm.IsDailyBriefingEnabled;
            }
        };
        briefing.Toggled += toggled;
        Bindings.Own(new ActionDisposable(() => briefing.Toggled -= toggled));
        group.Add(Card(Translator.SemanticIndex_DailyBriefingTitle, Translator.SemanticIndex_DailyBriefingDescription, WinoIconGlyph.DailyBriefing, briefing));

        var indicators = new WinoSettingsExpander(Ctx, Translator.SemanticIndex_IntelligenceIndicatorsTitle, Translator.SemanticIndex_IntelligenceIndicatorsDescription, WinoIconGlyph.NoteEdit);
        Watch(s => s.CanChangeIntelligencePreferences, on =>
        {
            foreach (var row in indicators.Items) SettingsBinder.SetEnabled(row, on);
        });
        group.Add(indicators);

        Watch(s => s.IsFeaturesGroupVisible, on => Show(group, on));
        Bind.Collection(vm.IntelligenceIndicatorSettings, () => RebuildIndicators(indicators));
        return group;
    }

    private void RebuildFolders(WinoSettingsExpander stack)
    {
        _folderScope?.Dispose();
        _folderScope = new BindingScope();
        var rows = Bind.Child(_folderScope);
        stack.ClearItems();
        var vm = VM;
        foreach (var item in vm.IntelligenceFolderCoverageItems.ToList())
        {
            var covered = Text(string.Empty, WinoTextStyle.Caption);
            var selected = Text(string.Empty, WinoTextStyle.CaptionStrong);
            Watch(rows, item, i => (i.CoveredSummary, i.SelectedCountSummary), s =>
            {
                covered.Text = s.CoveredSummary;
                selected.Text = s.SelectedCountSummary;
            });
            var remove = rows.Views.Click(rows.IconButton(WinoIconGlyph.Dismiss, Translator.Buttons_Delete), () =>
            {
                var command = vm.RemoveCoverageFolderCommand;
                if (command.CanExecute(item)) command.Execute(item);
            });
            var card = new WinoSettingsCard(Ctx, item.DisplayName, item.AvailableSummary, WinoIconGlyph.Folder, remove)
            {
                IsNested = true,
                BottomContent = Row(covered, selected)
            };
            Watch(rows, item, i => i.AvailableSummary, text => card.Description = text);
            AddRow(stack, card);
        }
    }

    private void RebuildIndicators(WinoSettingsExpander stack)
    {
        _indicatorScope?.Dispose();
        _indicatorScope = new BindingScope();
        var scope = _indicatorScope;
        var rows = Bind.Child(scope);
        stack.ClearItems();
        var vm = VM;
        foreach (var item in vm.IntelligenceIndicatorSettings.ToList())
        {
            var toggle = new WinoTextSwitch(Ctx, Translator.SemanticIndex_On, Translator.SemanticIndex_Off, item.DisplayName);
            Watch(rows, item, i => (i.IsVisible, i.IsBusy), s =>
            {
                toggle.IsOn = s.IsVisible;
                toggle.IsEnabled = !s.IsBusy && vm.CanChangeIntelligencePreferences;
            });
            EventHandler toggled = async (_, _) =>
            {
                if (!vm.IsPageReady || item.IsBusy || toggle.IsOn == item.IsVisible) return;
                item.IsBusy = true;
                try { item.IsVisible = await vm.SetIntelligenceIndicatorVisibilityAsync(item.Identifier, toggle.IsOn); }
                catch (Exception exception) { ReportError(exception); }
                finally
                {
                    item.IsBusy = false;
                    toggle.IsOn = item.IsVisible;
                }
            };
            toggle.Toggled += toggled;
            scope.Own(new ActionDisposable(() => toggle.Toggled -= toggled));
            AddRow(stack, new WinoSettingsCard(Ctx, item.DisplayName, null, WinoIconGlyph.None, toggle) { IsNested = true });
        }
    }

    /// <summary>Adds an item to the expander's detail page; the section draws the separators.</summary>
    private static void AddRow(WinoSettingsExpander stack, View row) => stack.Add(row);
}
