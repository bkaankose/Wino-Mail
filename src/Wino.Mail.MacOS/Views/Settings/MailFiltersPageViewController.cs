using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Mail filters of one account (Manage accounts › account › Mail filters, Windows MailFiltersPage):
/// the description with Refresh provider rules and New filter, the Gmail and provider-connect info
/// bars, then the rules grouped as "Wino on this device" and the provider's rules. Each card shows the
/// rule's natural-language summary and a meta line (manager · source folder · status), with the enable
/// switch, Move up / Move down, Edit, Duplicate and Delete. Navigation parameter: the account id.
/// </summary>
public sealed class MailFiltersPageViewController(MailFiltersPageViewModel viewModel, IFolderService folderService, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<MailFiltersPageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private readonly WinoSettingsGroup _localGroup = new(Translator.MailFilterEditor_WinoManaged);
    private readonly WinoSettingsGroup _providerGroup = new(string.Empty);
    private IReadOnlyDictionary<string, string> _folderNames = new Dictionary<string, string>();
    private NSView? _footer;
    private BindingScope? _rowScope;
    private bool _rebuildQueued;

    public string? PageTitle => Translator.MailFilters_Title;
    public string? PageDescription => Translator.MailFilters_Description;
    public event EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        // Header row under the window's title and description: Refresh provider rules and New filter, trailing.
        var spacer = WinoLayout.Spacer();
        var spinner = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, Indeterminate = true, ControlSize = NSControlSize.Small, IsDisplayedWhenStopped = false, TranslatesAutoresizingMaskIntoConstraints = false };
        Bind.Bind(vm, nameof(vm.IsBusy), s => s.IsBusy, busy => { if (busy) spinner.StartAnimation(null); else spinner.StopAnimation(null); });
        var refresh = Bind.Button(Translator.MailFilters_Refresh, () => Run(vm.LoadAsync(vm.Account?.Id ?? Guid.Empty)), icon: WinoIconGlyph.ArrowClockwise);
        Bind.Visible(refresh, vm, nameof(vm.IsProviderFiltersConnectedVisible), s => s.IsProviderFiltersConnectedVisible);
        Bind.Enabled(refresh, vm, nameof(vm.IsBusy), s => !s.IsBusy);
        var create = Bind.Button(Translator.MailFilters_NewFilter, vm.CreateFilter, primary: true, icon: WinoIconGlyph.Add);
        WinoAccessibility.Help(create, Translator.MailFilters_NewFilterDescription);
        var header = WinoLayout.HStack(WinoStyle.Space2, spacer, spinner, refresh, create);
        header.Alignment = NSLayoutAttribute.CenterY;
        header.EdgeInsets = new NSEdgeInsets(0, 1, 8, 0);
        Add(header);

        var gmail = InfoBar(WinoInfoBarSeverity.Informational, Translator.MailFilters_ProviderFeatureTitle, Translator.MailFilters_GmailProviderUnavailableDescription);
        Bind.Visible(Add(gmail), vm, nameof(vm.IsGmailAccount), s => s.IsGmailAccount);

        var connect = InfoBar(WinoInfoBarSeverity.Informational, Translator.MailFilters_ProviderFeatureTitle, Translator.MailFilters_ProviderFeatureConnectDescription);
        connect.ActionTitle = Translator.MailFilters_ProviderFeatureConnect;
        EventHandler connectHandler = (_, _) => Run(vm.ConnectProviderFiltersAsync());
        connect.ActionInvoked += connectHandler;
        Bindings.Own(new ActionDisposable(() => connect.ActionInvoked -= connectHandler));
        Bind.Visible(Add(connect), vm, nameof(vm.IsProviderFiltersConnectVisible), s => s.IsProviderFiltersConnectVisible);

        Add(EmptyState());

        Add(_localGroup);
        Add(_providerGroup);
        var footer = WinoStyle.Label(Translator.MailFilters_OrderFooter, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        var footerHost = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(footer, footerHost, 8, 1, 0, 0);
        _footer = Add(footerHost);

        // The ViewModel clears and refills the collection; coalesce those changes into one rebuild.
        Bind.Collection(vm.Filters, QueueRebuild);
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, _ => QueueRebuild());
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        await base.InitializeAsync(mode, parameter);
        var accountId = parameter switch
        {
            Guid guid => guid,
            string text when Guid.TryParse(text, out var parsed) => parsed,
            _ => Guid.Empty
        };
        if (accountId != Guid.Empty) await LoadFolderNamesAsync(accountId);
        await ViewModel.LoadAsync(parameter!);
    }

    /// <summary>Folder names by remote id, so Move actions in the summaries name their destination.</summary>
    private async Task LoadFolderNamesAsync(Guid accountId)
    {
        try
        {
            var folders = await folderService.GetFoldersAsync(accountId).ConfigureAwait(false);
            _folderNames = folders
                .Where(folder => !string.IsNullOrWhiteSpace(folder.RemoteFolderId))
                .GroupBy(folder => folder.RemoteFolderId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().FolderName, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) { ReportError(exception); }
    }

    /// <summary>MailFilters_EmptyTitle / EmptyDescription with the Filter glyph and a create button.</summary>
    private NSView EmptyState()
    {
        var vm = ViewModel;
        var icon = new WinoIconView(WinoIconGlyph.Filter, 36, WinoStyle.SecondaryText);
        WinoLayout.Size(icon, 36, 36);
        var title = WinoStyle.Label(Translator.MailFilters_EmptyTitle, NSFont.SystemFontOfSize(15, NSFontWeight.Semibold), WinoStyle.PrimaryText);
        var text = WinoStyle.Label(Translator.MailFilters_EmptyDescription, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        text.Alignment = NSTextAlignment.Center;
        text.PreferredMaxLayoutWidth = 360;
        var create = Bind.Button(Translator.MailFilters_NewFilter, vm.CreateFilter);
        var stack = WinoLayout.VStack(WinoStyle.Space2, icon, title, text, create);
        stack.Alignment = NSLayoutAttribute.CenterX;
        stack.SetCustomSpacing(12, text);

        var surface = new WinoSurfaceView
        {
            Fill = WinoSettingsStyle.CardFill,
            Stroke = WinoSettingsStyle.CardStroke,
            StrokeWidth = 1,
            CornerRadius = WinoSettingsStyle.CardRadius,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        surface.AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints(
        [
            stack.TopAnchor.ConstraintEqualTo(surface.TopAnchor, 48),
            stack.BottomAnchor.ConstraintEqualTo(surface.BottomAnchor, -48),
            stack.CenterXAnchor.ConstraintEqualTo(surface.CenterXAnchor),
            stack.LeadingAnchor.ConstraintGreaterThanOrEqualTo(surface.LeadingAnchor, 24)
        ]);
        WinoAccessibility.Label(surface, Translator.MailFilters_EmptyTitle);
        Bind.Visible(surface, vm, nameof(vm.IsEmpty), s => s.IsEmpty);
        return surface;
    }

    private void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        BeginInvokeOnMainThread(() =>
        {
            _rebuildQueued = false;
            Rebuild();
        });
    }

    private void Rebuild()
    {
        if (Bindings.IsDisposed) return;
        _rowScope?.Dispose();
        _rowScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_rowScope);
        _localGroup.Clear();
        _providerGroup.Clear();

        var items = ViewModel.Filters.ToList();
        var local = items.Where(item => !item.IsProviderManaged).OrderBy(item => item.Filter.Sequence).ToList();
        var provider = items.Where(item => item.IsProviderManaged).OrderBy(item => item.Filter.Sequence).ToList();
        for (var index = 0; index < local.Count; index++)
            _localGroup.Add(FilterCard(rows, local[index], index, local.Count));
        for (var index = 0; index < provider.Count; index++)
            _providerGroup.Add(FilterCard(rows, provider[index], index, provider.Count));

        _providerGroup.Title = string.Format(Translator.MailFilters_ProviderGroup, ProviderName());
        _localGroup.Hidden = local.Count == 0;
        _providerGroup.Hidden = provider.Count == 0;
        if (_footer is not null) _footer.Hidden = items.Count == 0;
    }

    private string ProviderName() => ViewModel.Account?.ProviderType == MailProviderType.Outlook ? "Outlook" : "Gmail";

    /// <summary>
    /// One rule card: the Devices / Cloud glyph, name, summary and the 11pt meta line (critical with a
    /// warning glyph when the rule needs attention), then the switch and the five icon buttons.
    /// </summary>
    private NSView FilterCard(SettingsBinder rows, MailFilterListItemViewModel item, int index, int count)
    {
        var filter = item.Filter;
        var name = filter.Name ?? string.Empty;

        var glyph = new WinoIconView(item.IsProviderManaged ? WinoIconGlyph.Cloud : WinoIconGlyph.Devices, WinoSettingsStyle.IconSize);
        WinoLayout.Size(glyph, WinoSettingsStyle.IconSize, WinoSettingsStyle.IconSize);

        var title = WinoStyle.Label(name, WinoSettingsStyle.CardTitle, WinoStyle.PrimaryText);
        var fallback = item.Summary;
        var summary = WinoStyle.Label(MailFilterPresentation.Summary(filter, _folderNames, fallback), WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        var metaText = WinoStyle.Label(MetaText(item), WinoStyle.Caption, item.HasWarning ? WinoStyle.Critical : WinoStyle.SecondaryText);
        var meta = WinoLayout.HStack(5, metaText);
        meta.Alignment = NSLayoutAttribute.CenterY;
        if (item.HasWarning)
        {
            var warning = new WinoIconView(WinoIconGlyph.Warning, 12, WinoStyle.Critical) { Colorful = false };
            WinoLayout.Size(warning, 12, 12);
            meta.InsertArrangedSubview(warning, 0);
        }
        var text = WinoLayout.VStack(1, title, summary, meta);
        text.Alignment = NSLayoutAttribute.Leading;
        text.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
        text.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        foreach (var label in new NSView[] { title, summary, meta })
        {
            label.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            label.WidthAnchor.ConstraintLessThanOrEqualTo(text.WidthAnchor).Active = true;
        }
        summary.WidthAnchor.ConstraintEqualTo(text.WidthAnchor).Active = true;

        var toggle = new NSSwitch { TranslatesAutoresizingMaskIntoConstraints = false, ControlSize = NSControlSize.Small };
        toggle.State = filter.IsEnabled ? 1 : 0;
        toggle.Enabled = item.CanEdit;
        Label(toggle, Translator.MailFilterEditor_Enabled, name);
        rows.OnActivated(toggle, () => Run(SetEnabledAsync(item, toggle, metaText)));

        var up = IconButton(rows, WinoIconGlyph.ChevronUp, Translator.MailFilters_MoveUp, name, () => Run(ViewModel.MoveFilterAsync(item, -1)));
        up.Enabled = item.CanReorder && index > 0;
        var down = IconButton(rows, WinoIconGlyph.ChevronDown, Translator.MailFilters_MoveDown, name, () => Run(ViewModel.MoveFilterAsync(item, 1)));
        down.Enabled = item.CanReorder && index < count - 1;
        var edit = IconButton(rows, WinoIconGlyph.Edit, Translator.MailFilters_Edit, name, () => ViewModel.EditFilter(item));
        edit.Enabled = item.CanEdit;
        var duplicate = IconButton(rows, WinoIconGlyph.Copy, Translator.MailFilters_Duplicate, name, () => ViewModel.DuplicateFilter(item));
        var delete = IconButton(rows, WinoIconGlyph.Delete, Translator.Buttons_Delete, name, () => Run(ViewModel.DeleteFilterAsync(item)));

        var buttons = WinoLayout.HStack(2, up, down, edit, duplicate, delete);
        var trailing = WinoLayout.HStack(WinoStyle.Space2, toggle, buttons);
        trailing.Alignment = NSLayoutAttribute.CenterY;
        trailing.SetHuggingPriority(751, NSLayoutConstraintOrientation.Horizontal);
        trailing.SetContentCompressionResistancePriority(751, NSLayoutConstraintOrientation.Horizontal);

        var content = WinoLayout.HStack(WinoSettingsStyle.IconGap, glyph, text, trailing);
        content.Alignment = NSLayoutAttribute.CenterY;

        var card = new WinoSurfaceView
        {
            Fill = WinoSettingsStyle.CardFill,
            Stroke = WinoSettingsStyle.CardStroke,
            StrokeWidth = 1,
            CornerRadius = WinoSettingsStyle.CardRadius,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Fill(content, card, WinoSettingsStyle.CardVerticalPadding, WinoSettingsStyle.CardPadding, WinoSettingsStyle.CardVerticalPadding, WinoSettingsStyle.CardPadding);
        card.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoSettingsStyle.CardMinHeight).Active = true;
        card.AccessibilityElement = true;
        card.AccessibilityRole = NSAccessibilityRoles.GroupRole;
        card.AccessibilityLabel = name;
        card.AccessibilityHelp = $"{summary.StringValue} {metaText.StringValue}";
        return card;
    }

    /// <summary>ManagementText · SourceFolderName · StatusText, skipping empty parts.</summary>
    private static string MetaText(MailFilterListItemViewModel item, string? status = null)
        => string.Join(" · ", new[] { item.ManagementText, item.Filter.SourceFolderName, status ?? item.StatusText }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>Toggles the rule; the ViewModel reverts the model on failure, so the switch and status follow it.</summary>
    private async Task SetEnabledAsync(MailFilterListItemViewModel item, NSSwitch toggle, NSTextField meta)
    {
        await ViewModel.SetEnabledAsync(item, toggle.State != 0);
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            toggle.State = item.Filter.IsEnabled ? 1 : 0;
            if (!item.HasWarning)
                meta.StringValue = MetaText(item, item.Filter.IsEnabled ? Translator.MailFilters_Enabled : Translator.MailFilters_Disabled);
        });
    }

    private static NSButton IconButton(SettingsBinder rows, WinoIconGlyph glyph, string action, string name, Action activated)
    {
        var button = rows.Button(string.Empty, activated, icon: glyph);
        button.ControlSize = NSControlSize.Small;
        Label(button, action, name);
        return button;
    }

    /// <summary>Icon-only controls carry the action and the rule name, as tooltip and accessible name.</summary>
    private static void Label(NSControl control, string action, string name)
    {
        var text = string.IsNullOrEmpty(name) ? action : $"{action}: {name}";
        control.ToolTip = text;
        WinoAccessibility.Label(control, text);
    }

    private async void Run(Task operation)
    {
        try { await operation; }
        catch (Exception exception) { ReportError(exception); }
    }
}
