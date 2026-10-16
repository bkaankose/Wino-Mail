using Android.Views;
using Android.Widget;
using Google.Android.Material.MaterialSwitch;
using Google.Android.Material.ProgressIndicator;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.ViewModels;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;
using ActionDisposable = Wino.Mail.Android.Infrastructure.ActionDisposable;

namespace Wino.Mail.Android.Views.Settings.Accounts;

/// <summary>
/// Mail filters of one account (iOS MailFiltersPageViewController, Windows MailFiltersPage): New filter and Refresh
/// provider rules, the Gmail and provider-connect info bars, then the rules grouped as "Wino on this device" and the
/// provider's rules. Each card shows the rule's natural-language summary and a meta line (manager · source folder ·
/// status) with the enable switch; Move up, Move down, Edit, Duplicate and Delete sit in a 48 dp button row under the
/// text. Navigation parameter: the account id. The ViewModel loads in the route's initializer; this view loads the
/// folder names the summaries use and keeps them with the page entry.
/// </summary>
public sealed class MailFiltersPageFragment : SettingsPageFragment<MailFiltersPageViewModel>, ISettingsPageTitleSource
{
    private const string FolderNamesKey = "MailFilters.FolderNames";

    private WinoSettingsGroup? _localGroup;
    private WinoSettingsGroup? _providerGroup;
    private View? _footer;
    private BindingScope? _rowScope;
    private bool _rebuildQueued;

    public string? PageTitle => Translator.MailFilters_Title;

    // The page's own description replaces the catalogue line (iOS PageDescription).
    protected override bool ShowsPageDescription => false;

    /// <summary>The route initializer (iOS InitializeAsync): Windows navigation, then the rules.</summary>
    public static async Task InitializeAsync(MailFiltersPageViewModel viewModel, Wino.Core.Domain.Models.Navigation.NavigationMode mode, object? parameter)
    {
        viewModel.OnNavigatedTo(mode, parameter);
        if (parameter is not null) await viewModel.LoadAsync(parameter);
    }

    private IReadOnlyDictionary<string, string> FolderNames
        => Entry?.State.TryGetValue(FolderNamesKey, out var names) == true && names is IReadOnlyDictionary<string, string> map ? map : new Dictionary<string, string>();

    protected override void BuildPage()
    {
        var vm = VM;
        Bindings.Own(new ActionDisposable(() => _rowScope?.Dispose()));
        AddIntro(Translator.MailFilters_Description);

        // Header row: New filter and Refresh provider rules.
        var spinner = new CircularProgressIndicator(Ctx) { Indeterminate = true, IndicatorSize = WinoTokens.Px(Ctx, Resource.Dimension.wino_glyph_l) };
        Bind.Visible(spinner, vm, nameof(vm.IsBusy), s => s.IsBusy);
        // New filter and Refresh provider rules are action rows; the busy ring sits after them.
        var refresh = ActionCard(Translator.MailFilters_Refresh, () => AccountSettingsParts.Run(vm.LoadAsync(vm.Account?.Id ?? Guid.Empty), ReportError));
        Bind.Visible(refresh, vm, nameof(vm.IsProviderFiltersConnectedVisible), s => s.IsProviderFiltersConnectedVisible);
        Bind.Enabled(refresh, vm, nameof(vm.IsBusy), s => !s.IsBusy);
        AddGroup(null, ActionCard(Translator.MailFilters_NewFilter, vm.CreateFilter), refresh);
        Add(spinner);

        var gmail = InfoBar(WinoInfoBarSeverity.Informational, Translator.MailFilters_ProviderFeatureTitle, Translator.MailFilters_GmailProviderUnavailableDescription);
        Bind.Visible(Add(gmail), vm, nameof(vm.IsGmailAccount), s => s.IsGmailAccount);

        var connect = InfoBar(WinoInfoBarSeverity.Informational, Translator.MailFilters_ProviderFeatureTitle, Translator.MailFilters_ProviderFeatureConnectDescription);
        connect.ActionTitle = Translator.MailFilters_ProviderFeatureConnect;
        EventHandler connectHandler = (_, _) => AccountSettingsParts.Run(vm.ConnectProviderFiltersAsync(), ReportError);
        connect.ActionInvoked += connectHandler;
        Bindings.Own(new ActionDisposable(() => connect.ActionInvoked -= connectHandler));
        Bind.Visible(Add(connect), vm, nameof(vm.IsProviderFiltersConnectVisible), s => s.IsProviderFiltersConnectVisible);

        Add(EmptyState());

        _localGroup = Add(Group(Translator.MailFilterEditor_WinoManaged));
        _providerGroup = Add(Group(string.Empty));
        _footer = Add(Text(Translator.MailFilters_OrderFooter, WinoTextStyle.Description));
        ((LinearLayout.LayoutParams)_footer.LayoutParameters!).TopMargin = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_s);

        // The ViewModel clears and refills the collection; coalesce those changes into one rebuild.
        Bind.Collection(vm.Filters, QueueRebuild);
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, _ => QueueRebuild());

        if (Entry is { } entry && !entry.State.ContainsKey(FolderNamesKey))
        {
            var accountId = entry.Parameter switch
            {
                Guid guid => guid,
                string text when Guid.TryParse(text, out var parsed) => parsed,
                _ => Guid.Empty
            };
            if (accountId != Guid.Empty) _ = LoadFolderNamesAsync(entry, accountId);
        }
    }

    /// <summary>Folder names by remote id, so Move actions in the summaries name their destination.</summary>
    private async Task LoadFolderNamesAsync(Infrastructure.PageEntry entry, Guid accountId)
    {
        try
        {
            if (MainApplication.Services?.GetService<IFolderService>() is not { } folderService) return;
            var folders = await folderService.GetFoldersAsync(accountId).ConfigureAwait(false);
            IReadOnlyDictionary<string, string> names = folders
                .Where(folder => !string.IsNullOrWhiteSpace(folder.RemoteFolderId))
                .GroupBy(folder => folder.RemoteFolderId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().FolderName, StringComparer.OrdinalIgnoreCase);
            await entry.Dispatcher.ExecuteOnUIThread(() =>
            {
                if (entry.IsReleased) return;
                entry.State[FolderNamesKey] = names;
                if (ReferenceEquals(entry.Fragment, this) && !Bindings.IsDisposed) QueueRebuild();
            });
        }
        catch (Exception exception) { ReportError(exception); }
    }

    /// <summary>MailFilters_EmptyTitle / EmptyDescription with the Filter glyph and a create button.</summary>
    private View EmptyState()
    {
        var vm = VM;
        var title = Text(Translator.MailFilters_EmptyTitle, WinoTextStyle.BodyLargeStrong);
        title.Gravity = GravityFlags.Center;
        var text = Text(Translator.MailFilters_EmptyDescription, WinoTextStyle.Description);
        text.Gravity = GravityFlags.Center;
        var create = Bind.Button(Translator.MailFilters_NewFilter, vm.CreateFilter);

        var stack = AccountSettingsParts.VColumn(Ctx);
        stack.SetGravity(GravityFlags.CenterHorizontal);
        stack.AddView(AccountSettingsParts.Glyph(Ctx, WinoIconGlyph.Filter, Resource.Dimension.wino_glyph_hero, WinoColors.SecondaryText(Ctx)));
        stack.AddView(title, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { TopMargin = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_s) });
        stack.AddView(text, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { TopMargin = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_s) });
        stack.AddView(create, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { TopMargin = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_m) });
        WinoTokens.Pad(stack, Resource.Dimension.wino_space_2xl, Resource.Dimension.wino_space_5xl, Resource.Dimension.wino_space_2xl, Resource.Dimension.wino_space_5xl);
        stack.Background = AccountSettingsParts.Surface(Ctx, WinoColors.CardFill(Ctx), WinoColors.CardStroke(Ctx));
        Bind.Visible(stack, vm, nameof(vm.IsEmpty), s => s.IsEmpty);
        return stack;
    }

    private void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Page.Post(() =>
        {
            _rebuildQueued = false;
            if (!Bindings.IsDisposed) Rebuild();
        });
    }

    private void Rebuild()
    {
        if (_localGroup is null || _providerGroup is null) return;
        _rowScope?.Dispose();
        _rowScope = new BindingScope();
        var rows = Bind.Child(_rowScope);
        _localGroup.Clear();
        _providerGroup.Clear();

        var items = VM.Filters.ToList();
        var local = items.Where(item => !item.IsProviderManaged).OrderBy(item => item.Filter.Sequence).ToList();
        var provider = items.Where(item => item.IsProviderManaged).OrderBy(item => item.Filter.Sequence).ToList();
        for (var index = 0; index < local.Count; index++)
            _localGroup.Add(FilterCard(rows, local[index], index, local.Count));
        for (var index = 0; index < provider.Count; index++)
            _providerGroup.Add(FilterCard(rows, provider[index], index, provider.Count));

        _providerGroup.Title = string.Format(Translator.MailFilters_ProviderGroup, ProviderName());
        _localGroup.Visibility = local.Count == 0 ? ViewStates.Gone : ViewStates.Visible;
        _providerGroup.Visibility = provider.Count == 0 ? ViewStates.Gone : ViewStates.Visible;
        if (_footer is not null) _footer.Visibility = items.Count == 0 ? ViewStates.Gone : ViewStates.Visible;
    }

    private string ProviderName() => VM.Account?.ProviderType == MailProviderType.Outlook ? "Outlook" : "Gmail";

    /// <summary>
    /// One rule card: the Devices / Cloud glyph, name, summary and the meta line (critical with a warning glyph when the
    /// rule needs attention) beside the switch, then the five 48 dp icon buttons.
    /// </summary>
    private View FilterCard(SettingsBinder rows, MailFilterListItemViewModel item, int index, int count)
    {
        var filter = item.Filter;
        var name = filter.Name ?? string.Empty;

        var top = AccountSettingsParts.HRow(Ctx);
        top.SetGravity(GravityFlags.Top);
        top.AddView(AccountSettingsParts.Glyph(Ctx, item.IsProviderManaged ? WinoIconGlyph.Cloud : WinoIconGlyph.Devices, Resource.Dimension.wino_glyph_l, WinoColors.SecondaryText(Ctx)),
            AccountSettingsParts.RowItem(Ctx));

        var text = AccountSettingsParts.VColumn(Ctx);
        text.AddView(Text(name, WinoTextStyle.BodyLarge, maxLines: 2));
        text.AddView(Text(MailFilterDisplay.GetSummary(filter, FolderNames, item.Summary), WinoTextStyle.Description));
        var meta = AccountSettingsParts.HRow(Ctx);
        var metaColor = item.HasWarning ? WinoColors.Critical(Ctx) : WinoColors.SecondaryText(Ctx);
        if (item.HasWarning) meta.AddView(AccountSettingsParts.Glyph(Ctx, WinoIconGlyph.Warning, Resource.Dimension.wino_glyph_xs, metaColor, colorful: false));
        var metaText = AccountSettingsParts.Label(Ctx, MetaText(item), WinoTextStyle.Caption, metaColor);
        meta.AddView(metaText, AccountSettingsParts.RowItem(Ctx, weight: 1, marginStart: item.HasWarning ? Resource.Dimension.wino_space_xs : 0));
        text.AddView(meta);
        top.AddView(text, AccountSettingsParts.RowItem(Ctx, weight: 1, marginStart: Resource.Dimension.wino_space_m));

        var toggle = WinoMaterialAccent.Tint(new MaterialSwitch(Ctx)
        {
            Checked = filter.IsEnabled,
            Enabled = item.CanEdit,
            ContentDescription = AccountSettingsParts.ActionLabel(Translator.MailFilterEditor_Enabled, name)
        });
        EventHandler<CompoundButton.CheckedChangeEventArgs> toggled = (_, args) =>
        {
            if (args.IsChecked == filter.IsEnabled) return;
            AccountSettingsParts.Run(SetEnabledAsync(item, toggle, metaText, args.IsChecked), ReportError);
        };
        toggle.CheckedChange += toggled;
        rows.Scope.Own(new ActionDisposable(() => toggle.CheckedChange -= toggled));
        top.AddView(toggle, AccountSettingsParts.RowItem(Ctx, marginStart: Resource.Dimension.wino_space_s));

        var up = IconButton(rows, WinoIconGlyph.ChevronUp, Translator.MailFilters_MoveUp, name, () => AccountSettingsParts.Run(VM.MoveFilterAsync(item, -1), ReportError));
        up.Enabled = item.CanReorder && index > 0;
        var down = IconButton(rows, WinoIconGlyph.ChevronDown, Translator.MailFilters_MoveDown, name, () => AccountSettingsParts.Run(VM.MoveFilterAsync(item, 1), ReportError));
        down.Enabled = item.CanReorder && index < count - 1;
        var edit = IconButton(rows, WinoIconGlyph.Edit, Translator.MailFilters_Edit, name, () => VM.EditFilter(item));
        edit.Enabled = item.CanEdit;
        var duplicate = IconButton(rows, WinoIconGlyph.Copy, Translator.MailFilters_Duplicate, name, () => VM.DuplicateFilter(item));
        var delete = IconButton(rows, WinoIconGlyph.Delete, Translator.Buttons_Delete, name, () => AccountSettingsParts.Run(VM.DeleteFilterAsync(item), ReportError));

        var buttons = AccountSettingsParts.HRow(Ctx);
        buttons.SetGravity(GravityFlags.End | GravityFlags.CenterVertical);
        foreach (var button in new View[] { up, down, edit, duplicate, delete }) buttons.AddView(button, AccountSettingsParts.RowItem(Ctx, marginStart: Resource.Dimension.wino_space_3xs));

        var card = AccountSettingsParts.VColumn(Ctx);
        card.AddView(top, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        card.AddView(buttons, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_2xs) });
        WinoTokens.Pad(card, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_m, Resource.Dimension.wino_space_s, Resource.Dimension.wino_space_2xs);
        card.SetMinimumHeight(WinoTokens.Px(Ctx, Resource.Dimension.wino_size_card_min));
        card.Background = AccountSettingsParts.Surface(Ctx, WinoColors.CardFill(Ctx), WinoColors.CardStroke(Ctx));
        return card;
    }

    /// <summary>ManagementText · SourceFolderName · StatusText, skipping empty parts.</summary>
    private static string MetaText(MailFilterListItemViewModel item, string? status = null)
        => string.Join(" · ", new[] { item.ManagementText, item.Filter.SourceFolderName, status ?? item.StatusText }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>Toggles the rule; the ViewModel reverts the model on failure, so the switch and status follow it.</summary>
    private async Task SetEnabledAsync(MailFilterListItemViewModel item, MaterialSwitch toggle, TextView meta, bool isEnabled)
    {
        await VM.SetEnabledAsync(item, isEnabled);
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (Bindings.IsDisposed) return;
            if (toggle.Checked != item.Filter.IsEnabled) toggle.Checked = item.Filter.IsEnabled;
            if (!item.HasWarning)
                meta.Text = MetaText(item, item.Filter.IsEnabled ? Translator.MailFilters_Enabled : Translator.MailFilters_Disabled);
        });
    }

    private View IconButton(SettingsBinder rows, WinoIconGlyph glyph, string action, string name, Action activated)
    {
        var button = AccountSettingsParts.GlyphButton(Ctx, glyph, AccountSettingsParts.ActionLabel(action, name));
        rows.Views.Click(button, activated);
        return button;
    }
}
