using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// An account's signatures (Windows SignatureManagementPage): the switch, the two defaults with the ViewModel's "None"
/// first, and the list with Add, Edit and Delete. The navigation parameter is the account id. Every change is written
/// by the ViewModel at once; leaving the page waits for the queued writes.
/// </summary>
internal sealed class SignatureManagementPage(SignatureManagementPageViewModel viewModel, WinoViewServices services, IAccountService accounts)
    : SettingsPageView<SignatureManagementPageViewModel>(viewModel, services), ISettingsPageTitleSource
{
    private readonly List<WinoSettingsGroup> _content = [];
    private WinoSettingsGroup? _list;
    private WinoSettingsGroup? _missing;
    private SettingsBinder? _rows;
    private string? _description;

#if DEBUG
    internal static SignatureManagementPage? DebugCurrent { get; private set; }
#endif

    public string? PageTitle => Translator.SettingsSignature_Title;

    public string? PageDescription => _description;

    public event EventHandler? PageTitleChanged;

    public override bool HasPendingWork => base.HasPendingWork || ViewModel.HasPendingPreferenceWrites;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        var error = Page.AddBanner();
        Settings.Bind(vm, nameof(vm.PreferenceError), static s => s.PreferenceError, text =>
        {
            error.SetTitle(text ?? string.Empty);
            error.SetRevealed(!string.IsNullOrEmpty(text));
        });

        _content.Add(AddGroup(null, Settings.Switch(vm, Translator.SettingsSignature_Title, Translator.SettingsSignature_EnableDescription, WinoIconGlyph.Signature,
            nameof(vm.IsSignatureEnabled), static s => s.IsSignatureEnabled, static (s, v) => s.IsSignatureEnabled = v)));

        // Both defaults list every signature with "None" first, as on Windows, and dim while signatures are off.
        var defaults = Page.AddGroup(Translator.SettingsSignature_SignatureDefaults, null,
            Settings.Choice<SignatureManagementPageViewModel, AccountSignature>(vm, Translator.SettingsSignature_ForNewMessages_Title,
                Translator.SettingsSignature_ForNewMessages_Description, WinoIconGlyph.NewMail,
                static s => s.Signatures, static signature => signature.Name ?? string.Empty,
                nameof(vm.SelectedSignatureForNewMessages), static s => s.SelectedSignatureForNewMessages, static (s, v) => s.SelectedSignatureForNewMessages = v,
                itemsCollection: vm.Signatures),
            Settings.Choice<SignatureManagementPageViewModel, AccountSignature>(vm, Translator.SettingsSignature_ForFollowingMessages_Title,
                Translator.SettingsSignature_ForFollowingMessages_Description, WinoIconGlyph.Reply,
                static s => s.Signatures, static signature => signature.Name ?? string.Empty,
                nameof(vm.SelectedSignatureForFollowingMessages), static s => s.SelectedSignatureForFollowingMessages, static (s, v) => s.SelectedSignatureForFollowingMessages = v,
                itemsCollection: vm.Signatures));
        Settings.Enabled(defaults.Root, vm, nameof(vm.IsSignatureEnabled), static s => s.IsSignatureEnabled);
        _content.Add(defaults);

        _list = Page.AddGroup(Translator.SettingsSignature_Signatures);
        _list.HidesWhenEmpty = false;
        _list.HeaderSuffix = Settings.Button(Translator.SettingsSignature_AddCustomSignature_Button, vm.OpenSignatureEditorCreateCommand, suggested: true);
        _content.Add(_list);
        Settings.Collection(vm.Signatures, RebuildList);
        Settings.Bind(vm, nameof(vm.SelectedSignatureForNewMessages), static s => s.SelectedSignatureForNewMessages, _ => RebuildList());

        var missing = Adw.StatusPage.New();
        missing.SetTitle(Translator.GeneralTitle_Error);
        missing.AddCssClass("compact");
        _missing = Page.AddGroup(null, null, WinoSettingsRows.Custom(missing, Translator.GeneralTitle_Error));
        _missing.Root.SetVisible(false);
        foreach (var group in _content) group.Root.SetVisible(false);
#if DEBUG
        DebugCurrent = this;
#endif
    }

    protected override async Task NavigateAsync(NavigationMode mode, object? parameter)
    {
        if (parameter is Guid accountId) Observe(LoadDescriptionAsync(accountId));
        await ViewModel.InitializeAsync(mode, parameter);
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (IsReleased) return;
            var loaded = ViewModel.HasLoadedAccount;
            foreach (var group in _content) group.Root.SetVisible(loaded);
            _missing?.Root.SetVisible(!loaded);
        });
    }

    protected override async Task DeactivateAsync()
    {
#if DEBUG
        if (ReferenceEquals(DebugCurrent, this)) DebugCurrent = null;
#endif
        await base.DeactivateAsync();
        await ViewModel.DrainPreferenceWritesAsync();
    }

    private async Task LoadDescriptionAsync(Guid accountId)
    {
        var account = await accounts.GetAccountAsync(accountId);
        if (account is null) return;
        var name = account.Name ?? string.Empty;
        _description = string.IsNullOrWhiteSpace(account.Address) || account.Address == name ? name : $"{name} · {account.Address}";
        PageTitleChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildList()
    {
        if (_list is null || IsReleased) return;
        _rows?.Scope.Dispose();
        _list.Clear();
        var rows = _rows = Settings.Child();
        var vm = ViewModel;
        var defaultId = vm.SelectedSignatureForNewMessages?.Id;
        var signatures = vm.Signatures.Where(signature => signature is not null && signature.Id != vm.EmptyGuid).ToList();
        if (signatures.Count == 0)
        {
            var empty = Adw.StatusPage.New();
            empty.SetTitle(Translator.SettingsSignature_EmptyTitle);
            empty.SetDescription(Translator.SettingsSignature_EmptyDescription);
            empty.AddCssClass("compact");
            _list.Add(WinoSettingsRows.Custom(empty, Translator.SettingsSignature_EmptyTitle));
            return;
        }

        foreach (var signature in signatures)
        {
            var name = signature.Name ?? string.Empty;
            var isDefault = defaultId == signature.Id;
            var row = WinoSettingsRows.Action(name, HtmlPreviewText.From(signature.HtmlBody), WinoIconGlyph.Signature);
            row.SetSubtitleLines(1);
            if (isDefault)
            {
                var badge = Gtk.Label.New(Translator.SettingsSignature_DefaultBadge);
                badge.AddCssClass("caption");
                badge.AddCssClass("accent");
                badge.SetValign(Gtk.Align.Center);
                WinoSettingsRows.Suffix(row, badge);
                WinoA11y.Rename(row, $"{name}, {Translator.SettingsSignature_DefaultBadge}");
            }
            var edit = WinoSettingsRows.Suffix(row, rows.Button(Translator.Buttons_Edit, vm.OpenSignatureEditorEditCommand, () => signature));
            edit.AddCssClass("flat");
            edit.SetValign(Gtk.Align.Center);
            WinoA11y.Rename(edit, $"{Translator.SettingsSignature_EditSignature_Title}: {name}");
            var delete = WinoSettingsRows.Suffix(row, rows.Button(Translator.Buttons_Delete, vm.DeleteSignatureCommand, () => signature, destructive: true));
            delete.AddCssClass("flat");
            delete.SetValign(Gtk.Align.Center);
            WinoA11y.Rename(delete, $"{Translator.SettingsSignature_DeleteSignature_Title}: {name}");
            _list.Add(row);
        }
    }
}
