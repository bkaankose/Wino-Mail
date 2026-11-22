using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Presentation;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// An account's mail filters (Windows MailFiltersPage): the provider notices, New filter and Refresh, the provider and
/// Wino sections in run order with enable, Move up / Move down, Edit, Duplicate and Delete per filter, and the empty
/// state. The navigation parameter is the account id.
/// </summary>
internal sealed class MailFiltersPage(MailFiltersPageViewModel viewModel, WinoViewServices services, IFolderService folders)
    : SettingsPageView<MailFiltersPageViewModel>(viewModel, services), ISettingsPageTitleSource
{
    private Dictionary<string, string> _folderNames = new(StringComparer.OrdinalIgnoreCase);
    private WinoSettingsGroup? _provider;
    private WinoSettingsGroup? _local;
    private WinoSettingsGroup? _empty;
    private SettingsBinder? _rows;

#if DEBUG
    internal static MailFiltersPage? DebugCurrent { get; private set; }
#endif

    public string? PageTitle => Translator.MailFilters_Title;

    public string? PageDescription => Translator.MailFilters_Description;

    public event EventHandler? PageTitleChanged { add { } remove { } }

    protected override void BuildPage()
    {
        var vm = ViewModel;
        var gmail = Page.AddBanner(Translator.MailFilters_GmailProviderUnavailableDescription);
        Settings.Bind(vm, nameof(vm.IsGmailAccount), static s => s.IsGmailAccount, gmail.SetRevealed);
        var connect = Page.AddBanner(Translator.MailFilters_ProviderFeatureConnectDescription);
        connect.SetButtonLabel(Translator.MailFilters_ProviderFeatureConnect);
        Settings.Own(GtkSignals.Connect<Adw.Banner>(h => connect.OnButtonClicked += h, h => connect.OnButtonClicked -= h,
            (_, _) => Observe(ViewModel.ConnectProviderFiltersAsync())));
        Settings.Bind(vm, nameof(vm.IsProviderFiltersConnectVisible), static s => s.IsProviderFiltersConnectVisible, connect.SetRevealed);

        var header = WinoSettingsRows.Action(Translator.MailFilters_NewFilter, Translator.MailFilters_NewFilterDescription, WinoIconGlyph.Add);
        var spinner = WinoSettingsRows.Suffix(header, Adw.Spinner.New());
        Settings.Visible(spinner, vm, nameof(vm.IsBusy), static s => s.IsBusy);
        var refresh = WinoSettingsRows.Suffix(header, Settings.Button(Translator.MailFilters_Refresh,
            () => Observe(ViewModel.LoadAsync(ViewModel.Account?.Id ?? Guid.Empty))));
        refresh.SetValign(Gtk.Align.Center);
        Settings.Visible(refresh, vm, nameof(vm.IsProviderFiltersConnectedVisible), static s => s.IsProviderFiltersConnectedVisible);
        var create = WinoSettingsRows.Suffix(header, Settings.Button(Translator.MailFilters_NewFilter, ViewModel.CreateFilter, suggested: true));
        create.SetValign(Gtk.Align.Center);
        AddGroup(null, header);

        _provider = Page.AddGroup(string.Format(Translator.MailFilters_ProviderGroup, ProviderName()));
        _local = Page.AddGroup(Translator.MailFilterEditor_WinoManaged, Translator.MailFilters_OrderFooter);

        var status = Adw.StatusPage.New();
        status.SetTitle(Translator.MailFilters_EmptyTitle);
        status.SetDescription(Translator.MailFilters_EmptyDescription);
        status.AddCssClass("compact");
        var emptyCreate = Settings.Button(Translator.MailFilters_NewFilter, ViewModel.CreateFilter, suggested: true);
        emptyCreate.SetHalign(Gtk.Align.Center);
        emptyCreate.AddCssClass("pill");
        status.SetChild(emptyCreate);
        _empty = Page.AddGroup(null, null, WinoSettingsRows.Custom(status, Translator.MailFilters_EmptyTitle));
        _empty.Root.SetVisible(false);
        Settings.Bind(vm, nameof(vm.IsEmpty), static s => s.IsEmpty, _ => RefreshEmpty());
        Settings.Bind(vm, nameof(vm.IsBusy), static s => s.IsBusy, _ => RefreshEmpty());
        Settings.Bind(vm, nameof(vm.Account), static s => s.Account, _ =>
        {
            if (_provider is not null) _provider.Title = string.Format(Translator.MailFilters_ProviderGroup, ProviderName());
        });
        Settings.Collection(vm.Filters, Rebuild);
#if DEBUG
        DebugCurrent = this;
#endif
    }

    protected override async Task NavigateAsync(NavigationMode mode, object? parameter)
    {
        await base.NavigateAsync(mode, parameter);
        if (parameter is Guid accountId)
        {
            var names = (await folders.GetFoldersAsync(accountId))
                .Where(folder => !string.IsNullOrWhiteSpace(folder.RemoteFolderId))
                .GroupBy(folder => folder.RemoteFolderId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().ResolvedDisplayName ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            await Dispatcher.ExecuteOnUIThread(() => _folderNames = names);
        }
        await ViewModel.LoadAsync(parameter ?? Guid.Empty);
    }

    protected override Task DeactivateAsync()
    {
#if DEBUG
        if (ReferenceEquals(DebugCurrent, this)) DebugCurrent = null;
#endif
        return base.DeactivateAsync();
    }

    private string ProviderName() => ViewModel.Account?.ProviderType == MailProviderType.Outlook ? "Outlook" : "Gmail";

    private void RefreshEmpty() => _empty?.Root.SetVisible(ViewModel.IsEmpty && !ViewModel.IsBusy);

    private void Rebuild()
    {
        if (_provider is null || _local is null || IsReleased) return;
        _rows?.Scope.Dispose();
        _provider.Clear();
        _local.Clear();
        var rows = _rows = Settings.Child();
        foreach (var (group, provider) in ((WinoSettingsGroup, bool)[])[(_provider, true), (_local, false)])
        {
            var items = ViewModel.Filters.Where(item => item is not null && item.IsProviderManaged == provider)
                .OrderBy(item => item.Filter.Sequence).ToList();
            for (var index = 0; index < items.Count; index++) group.Add(Row(rows, items[index], index, items.Count));
        }
    }

    private Adw.ActionRow Row(SettingsBinder rows, MailFilterListItemViewModel item, int index, int count)
    {
        var filter = item.Filter;
        var name = filter.Name ?? string.Empty;
        var row = WinoSettingsRows.Action(name, null);
        row.SetTitleLines(1);
        row.SetSubtitle(Subtitle(item, null));
        row.AddPrefix(MailFilterPresentation.Tile(item.IsProviderManaged ? WinoIconGlyph.Cloud : WinoIconGlyph.Devices, "accent"));
        if (item.HasWarning)
        {
            var warning = WinoIcons.Label(WinoIconGlyph.Warning, 14);
            warning.AddCssClass("error");
            warning.SetTooltipText(item.StatusText);
            WinoA11y.Label(warning, item.StatusText);
            row.AddPrefix(warning);
        }

        if (item.CanEdit)
        {
            var toggle = Gtk.Switch.New();
            toggle.SetValign(Gtk.Align.Center);
            toggle.SetActive(filter.IsEnabled);
            WinoA11y.Label(toggle, $"{Translator.MailFilterEditor_Enabled}: {name}");
            WinoSettingsRows.Suffix(row, toggle);
            var applying = false;
            rows.Own(GtkSignals.OnNotify(toggle, "active", () =>
            {
                if (applying || toggle.GetActive() == filter.IsEnabled) return;
                Observe(SetEnabledAsync(item, toggle, row, () => applying = true, () => applying = false));
            }));
        }

        if (item.CanReorder)
        {
            var up = Button(rows, row, WinoIconGlyph.ChevronUp, Translator.MailFilters_MoveUp, name, () => Observe(ViewModel.MoveFilterAsync(item, -1)));
            up.SetSensitive(index > 0);
            var down = Button(rows, row, WinoIconGlyph.ChevronDown, Translator.MailFilters_MoveDown, name, () => Observe(ViewModel.MoveFilterAsync(item, 1)));
            down.SetSensitive(index < count - 1);
        }
        if (item.CanEdit)
        {
            Button(rows, row, WinoIconGlyph.Edit, Translator.MailFilters_Edit, name, () => ViewModel.EditFilter(item));
            row.SetActivatable(true);
            rows.Own(GtkSignals.Connect<Adw.ActionRow>(h => row.OnActivated += h, h => row.OnActivated -= h, (_, _) => ViewModel.EditFilter(item)));
        }
        Button(rows, row, WinoIconGlyph.Copy, Translator.MailFilters_Duplicate, name, () => ViewModel.DuplicateFilter(item));
        Button(rows, row, WinoIconGlyph.Delete, Translator.Buttons_Delete, name, () => Observe(ViewModel.DeleteFilterAsync(item)));
        WinoA11y.Rename(row, name);
        return row;
    }

    private string Subtitle(MailFilterListItemViewModel item, string? status)
    {
        var meta = MailFilterPresentation.MetaText(item, status);
        var summary = MailFilterDisplay.GetSummary(item.Filter, _folderNames, item.Summary ?? string.Empty);
        return string.IsNullOrWhiteSpace(summary) ? meta : $"{summary}\n{meta}";
    }

    private static Gtk.Button Button(SettingsBinder rows, Adw.ActionRow row, WinoIconGlyph glyph, string verb, string name, Action action)
    {
        var button = WinoSettingsRows.Suffix(row, WinoSettingsRows.GlyphButton(glyph, $"{verb}: {name}"));
        button.SetTooltipText(verb);
        rows.Own(GtkSignals.OnClicked(button, action));
        return button;
    }

    private async Task SetEnabledAsync(MailFilterListItemViewModel item, Gtk.Switch toggle, Adw.ActionRow row, Action beginApply, Action endApply)
    {
        await ViewModel.SetEnabledAsync(item, toggle.GetActive());
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (IsReleased) return;
            beginApply();
            toggle.SetActive(item.Filter.IsEnabled);
            endApply();
            row.SetSubtitle(Subtitle(item, item.Filter.IsEnabled ? Translator.MailFilters_Enabled : Translator.MailFilters_Disabled));
        });
    }

#if DEBUG
    internal string DebugDescribe()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"busy={ViewModel.IsBusy} empty={ViewModel.IsEmpty} emptyVisible={_empty?.Root.GetVisible()} provider={_provider?.Count} local={_local?.Count}");
        foreach (var item in ViewModel.Filters.OrderBy(item => item.Filter.Sequence))
            text.AppendLine($"{item.Filter.Sequence}|{item.Filter.Name}|enabled={item.Filter.IsEnabled}|{MailFilterPresentation.MetaText(item)}|{MailFilterDisplay.GetSummary(item.Filter, _folderNames, item.Summary ?? string.Empty)}|edit={item.CanEdit}|reorder={item.CanReorder}");
        return text.ToString();
    }
#endif
}
