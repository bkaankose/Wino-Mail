using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Mail filters of one account (Mac MailFiltersPageViewController, Windows MailFiltersPage): Refresh provider rules
/// and New filter, the Gmail and provider-connect info bars, then the rules grouped as "Wino on this device" and the
/// provider's rules. Each card shows the rule's natural-language summary and a meta line (manager · source folder ·
/// status) with the enable switch; Move up, Move down, Edit, Duplicate and Delete sit in a 44pt button row under the
/// text at phone width. Navigation parameter: the account id.
/// </summary>
public sealed class MailFiltersPageViewController(MailFiltersPageViewModel viewModel, IFolderService folderService, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<MailFiltersPageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private readonly WinoSettingsGroup _localGroup = new(Translator.MailFilterEditor_WinoManaged);
    private readonly WinoSettingsGroup _providerGroup = new(string.Empty);
    private IReadOnlyDictionary<string, string> _folderNames = new Dictionary<string, string>();
    private UIView? _footer;
    private BindingScope? _rowScope;
    private bool _rebuildQueued;

    public string? PageTitle => Translator.MailFilters_Title;
    public string? PageDescription => Translator.MailFilters_Description;
    /// <summary>The title is fixed.</summary>
    public event EventHandler? PageTitleChanged { add { } remove { } }

    protected override void BuildPage()
    {
        var vm = ViewModel;

        // Header row under the title and description: Refresh provider rules and New filter.
        var spinner = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium) { HidesWhenStopped = true, TranslatesAutoresizingMaskIntoConstraints = false };
        Bind.Bind(vm, nameof(vm.IsBusy), s => s.IsBusy, busy => { if (busy) spinner.StartAnimating(); else spinner.StopAnimating(); });
        var refresh = Bind.Button(Translator.MailFilters_Refresh, () => Run(vm.LoadAsync(vm.Account?.Id ?? Guid.Empty)), icon: WinoIconGlyph.ArrowClockwise);
        Bind.Visible(refresh, vm, nameof(vm.IsProviderFiltersConnectedVisible), s => s.IsProviderFiltersConnectedVisible);
        Bind.Enabled(refresh, vm, nameof(vm.IsBusy), s => !s.IsBusy);
        var create = Bind.Button(Translator.MailFilters_NewFilter, vm.CreateFilter, primary: true, icon: WinoIconGlyph.Add);
        WinoAccessibility.Help(create, Translator.MailFilters_NewFilterDescription);
        foreach (var button in new[] { refresh, create }) button.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        var header = WinoLayout.HStack(WinoStyle.Space2, create, refresh, spinner, WinoLayout.Spacer());
        WinoLayout.Insets(header, 0, 1, 8, 0);
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
        var footerHost = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
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
        await ViewModel.LoadAsync(parameter);
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
    private UIView EmptyState()
    {
        var vm = ViewModel;
        var icon = new WinoIconView(WinoIconGlyph.Filter, 36, WinoStyle.SecondaryText);
        WinoLayout.Size(icon, 36, 36);
        var title = WinoStyle.Label(Translator.MailFilters_EmptyTitle, WinoStyle.Scaled(15, UIFontWeight.Semibold), WinoStyle.PrimaryText, 0);
        title.TextAlignment = UITextAlignment.Center;
        var text = WinoStyle.Label(Translator.MailFilters_EmptyDescription, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        text.TextAlignment = UITextAlignment.Center;
        text.WidthAnchor.ConstraintLessThanOrEqualTo(360).Active = true;
        var create = Bind.Button(Translator.MailFilters_NewFilter, vm.CreateFilter);
        create.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        var stack = WinoLayout.VStack(WinoStyle.Space2, icon, title, text, create);
        stack.Alignment = UIStackViewAlignment.Center;
        stack.SetCustomSpacing(12, text);

        var surface = new WinoSurfaceView
        {
            Fill = WinoSettingsStyle.CardFill,
            Stroke = WinoSettingsStyle.CardStroke,
            StrokeWidth = 1,
            CornerRadius = WinoSettingsStyle.CardRadius,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        stack.TranslatesAutoresizingMaskIntoConstraints = false;
        surface.AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints(
        [
            stack.TopAnchor.ConstraintEqualTo(surface.TopAnchor, 40),
            stack.BottomAnchor.ConstraintEqualTo(surface.BottomAnchor, -40),
            stack.CenterXAnchor.ConstraintEqualTo(surface.CenterXAnchor),
            stack.LeadingAnchor.ConstraintGreaterThanOrEqualTo(surface.LeadingAnchor, 24)
        ]);
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
    /// One rule card: the Devices / Cloud glyph, name, summary and the meta line (critical with a warning glyph when the
    /// rule needs attention) beside the switch, then the five 44pt icon buttons.
    /// </summary>
    private UIView FilterCard(SettingsBinder rows, MailFilterListItemViewModel item, int index, int count)
    {
        var filter = item.Filter;
        var name = filter.Name ?? string.Empty;

        var glyph = new WinoIconView(item.IsProviderManaged ? WinoIconGlyph.Cloud : WinoIconGlyph.Devices, WinoSettingsStyle.IconSize);
        WinoLayout.Size(glyph, WinoSettingsStyle.IconSize, WinoSettingsStyle.IconSize);

        var title = WinoStyle.Label(name, WinoSettingsStyle.CardTitle, WinoStyle.PrimaryText, 2);
        var summary = WinoStyle.Label(MailFilterDisplay.GetSummary(filter, _folderNames, item.Summary), WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        var metaText = WinoStyle.Label(MetaText(item), WinoStyle.Caption, item.HasWarning ? WinoStyle.Critical : WinoStyle.SecondaryText, 0);
        var meta = WinoLayout.HStack(5, metaText);
        if (item.HasWarning)
        {
            var warning = new WinoIconView(WinoIconGlyph.Warning, 12, WinoStyle.Critical) { Colorful = false };
            WinoLayout.Size(warning, 12, 12);
            meta.InsertArrangedSubview(warning, 0);
        }
        var text = WinoLayout.VStack(1, title, summary, meta);
        text.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Priorities(text, UILayoutConstraintAxis.Horizontal, 1, 250);
        foreach (var label in new UIView[] { title, summary, metaText }) WinoLayout.Priorities(label, UILayoutConstraintAxis.Horizontal, 1, 250);

        var toggle = new UISwitch { On = filter.IsEnabled, Enabled = item.CanEdit, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Priorities(toggle, UILayoutConstraintAxis.Horizontal, 751, 751);
        WinoAccessibility.Label(toggle, ActionLabel(Translator.MailFilterEditor_Enabled, name));
        rows.OnActivated(toggle, () => Run(SetEnabledAsync(item, toggle, metaText)), UIControlEvent.ValueChanged);

        var up = IconButton(rows, WinoIconGlyph.ChevronUp, Translator.MailFilters_MoveUp, name, () => Run(ViewModel.MoveFilterAsync(item, -1)));
        up.Enabled = item.CanReorder && index > 0;
        var down = IconButton(rows, WinoIconGlyph.ChevronDown, Translator.MailFilters_MoveDown, name, () => Run(ViewModel.MoveFilterAsync(item, 1)));
        down.Enabled = item.CanReorder && index < count - 1;
        var edit = IconButton(rows, WinoIconGlyph.Edit, Translator.MailFilters_Edit, name, () => ViewModel.EditFilter(item));
        edit.Enabled = item.CanEdit;
        var duplicate = IconButton(rows, WinoIconGlyph.Copy, Translator.MailFilters_Duplicate, name, () => ViewModel.DuplicateFilter(item));
        var delete = IconButton(rows, WinoIconGlyph.Delete, Translator.Buttons_Delete, name, () => Run(ViewModel.DeleteFilterAsync(item)));

        var top = WinoLayout.HStack(WinoSettingsStyle.IconGap, glyph, text, toggle);
        top.Alignment = UIStackViewAlignment.Top;
        var buttons = WinoLayout.HStack(2, WinoLayout.Spacer(), up, down, edit, duplicate, delete);
        var content = WinoLayout.VStack(WinoStyle.Space1, top, buttons);
        content.Alignment = UIStackViewAlignment.Fill;

        var card = new WinoSurfaceView
        {
            Fill = WinoSettingsStyle.CardFill,
            Stroke = WinoSettingsStyle.CardStroke,
            StrokeWidth = 1,
            CornerRadius = WinoSettingsStyle.CardRadius,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Fill(content, card, WinoSettingsStyle.CardVerticalPadding + 2, WinoSettingsStyle.CardPadding, 4, 8);
        card.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoSettingsStyle.CardMinHeight).Active = true;
        return card;
    }

    /// <summary>ManagementText · SourceFolderName · StatusText, skipping empty parts.</summary>
    private static string MetaText(MailFilterListItemViewModel item, string? status = null)
        => string.Join(" · ", new[] { item.ManagementText, item.Filter.SourceFolderName, status ?? item.StatusText }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>Toggles the rule; the ViewModel reverts the model on failure, so the switch and status follow it.</summary>
    private async Task SetEnabledAsync(MailFilterListItemViewModel item, UISwitch toggle, UILabel meta)
    {
        await ViewModel.SetEnabledAsync(item, toggle.On);
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            toggle.On = item.Filter.IsEnabled;
            if (!item.HasWarning)
                meta.Text = MetaText(item, item.Filter.IsEnabled ? Translator.MailFilters_Enabled : Translator.MailFilters_Disabled);
        });
    }

    private static SettingsGlyphButton IconButton(SettingsBinder rows, WinoIconGlyph glyph, string action, string name, Action activated)
    {
        var button = new SettingsGlyphButton(glyph, ActionLabel(action, name));
        rows.OnActivated(button, activated);
        return button;
    }

    /// <summary>Icon-only controls carry the action and the rule name as their accessible name.</summary>
    private static string ActionLabel(string action, string name) => string.IsNullOrEmpty(name) ? action : $"{action}: {name}";

    private async void Run(Task operation)
    {
        try { await operation; }
        catch (Exception exception) { ReportError(exception); }
    }
}
