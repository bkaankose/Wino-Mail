using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>The address and discovery steps (Windows AccountSetupStepTemplateSelector: Identity, Resolving, NotFound).</summary>
internal sealed partial class AccountSetupView
{
    private readonly List<WinoColorSwatches> _swatches = [];
    private readonly List<AccountSetupShelfItem> _shelfItems = [];
    private Adw.EntryRow _email = null!;
    private Adw.EntryRow _localName = null!;
    private List<AccountSetupProviderShelfItem>? _shownShelf;

    /// <summary>
    /// The providers Wino connects to, the address with the recognised provider's mark (with Skip while importing
    /// accounts from other mail clients), or a local account's name.
    /// </summary>
    private Gtk.Widget BuildAddressStep()
    {
        var vm = ViewModel;
        var shelf = Adw.WrapBox.New();
        shelf.SetChildSpacing(12);
        shelf.SetLineSpacing(12);
        shelf.SetJustify(Adw.JustifyMode.None);
        shelf.SetHalign(Gtk.Align.Center);
        WinoA11y.Label(shelf, Translator.AccountSetup_Shelf_Header);
        _form.When(shelf, s => s.IsProviderShelfVisible);
        _form.Apply(() => RenderShelf(shelf));

        _email = _form.Entry(Translator.AccountSetup_EmailHeader, nameof(vm.EmailAddress), s => s.EmailAddress, (s, v) => s.EmailAddress = v, Gtk.InputPurpose.Email);
        WinoA11y.Rename(_email, Translator.AccountSetup_EmailHeader);
        var mark = new WinoIcon(WinoIconGlyph.Mail, 16, WinoIconMode.Colorful);
        _email.AddPrefix(mark.Root);
        _form.Apply(() =>
        {
            mark.Root.SetVisible(ViewModel.HasMatchedProvider);
            mark.SetIcon(ViewModel.MatchedProviderGlyph);
        });
        _form.OnEnter(_email, vm.PrimaryCommand);
        var emailGroup = OnboardingUi.Group();
        emailGroup.Add(_email);
        var shelfCaption = _form.Text(OnboardingUi.Caption(), s => s.ProviderShelfCaption);
        _form.When(shelfCaption, s => !s.HasIdentityError && !string.IsNullOrEmpty(s.ProviderShelfCaption));
        var identityError = _form.Text(OnboardingUi.Error(), s => s.IdentityError);
        _form.When(identityError, s => s.HasIdentityError);
        var email = _form.When(OnboardingUi.Column(6, emailGroup, shelfCaption, identityError), s => s.IsEmailFieldVisible);

        var (nameGroup, name) = BuildNameRow();
        _localName = name;
        _form.OnEnter(name, vm.PrimaryCommand);
        var nameError = _form.Text(OnboardingUi.Error(), s => s.AccountNameError);
        _form.When(nameError, s => s.HasAccountNameError);
        var local = _form.When(OnboardingUi.Column(6, nameGroup, nameError), s => s.IsLocalOnlyMode);

        // An account picked from another mail client can be left out; the next one's setup opens.
        var skip = _form.When(_form.Link(vm.SkipImportedAccountText, vm.SkipImportedAccountCommand), s => s.IsSkipImportedAccountVisible);
        _form.Apply(() => OnboardingUi.SetLabel(skip, ViewModel.SkipImportedAccountText));

        return OnboardingUi.Column(16, shelf, email, skip, local);
    }

    private void RenderShelf(Adw.WrapBox shelf)
    {
        var items = ViewModel.ProviderShelfItems;
        if (ReferenceEquals(items, _shownShelf)) return;
        _shownShelf = items;
        while (_shelfItems.Count > items.Count)
        {
            shelf.Remove(_shelfItems[^1].Root);
            _shelfItems.RemoveAt(_shelfItems.Count - 1);
        }
        while (_shelfItems.Count < items.Count)
        {
            var item = new AccountSetupShelfItem();
            _shelfItems.Add(item);
            shelf.Append(item.Root);
        }
        for (var index = 0; index < items.Count; index++) _shelfItems[index].Apply(items[index]);
    }

    /// <summary>The account name with the account colours under it (local setup and the review).</summary>
    private (Gtk.Widget Group, Adw.EntryRow Name) BuildNameRow()
    {
        var vm = ViewModel;
        var name = _form.Entry(Translator.AccountSetup_AccountNameHeader, nameof(vm.AccountName), s => s.AccountName, (s, v) => s.AccountName = v);
        var colors = new WinoColorSwatches(Translator.AccountSetup_AccountNameHeader);
        _swatches.Add(colors);
        colors.SelectionChanged += hex => _form.Run(ViewModel.SelectColorCommand, hex);
        _form.Apply(() =>
        {
            var hex = ViewModel.SelectedColor?.Hex;
            if (!string.Equals(colors.SelectedHex, hex, StringComparison.OrdinalIgnoreCase)) colors.SelectedHex = hex;
        });
        var group = OnboardingUi.Group();
        group.Add(name);
        group.Add(WinoSettingsRows.Custom(colors.Root, Translator.AccountSetup_AccountNameHeader));
        return (group, name);
    }

    private void ApplyPalette()
    {
        var palette = ViewModel.AvailableColors.Select(color => color.Hex).ToArray();
        foreach (var swatches in _swatches) swatches.Colors = palette;
    }

    private void ReleaseSwatches()
    {
        foreach (var swatches in _swatches) swatches.Release();
        _swatches.Clear();
    }

    private Gtk.Widget BuildDiscoveryStep()
    {
        var vm = ViewModel;
        var slow = _form.When(OnboardingUi.Caption(vm.DiscoverySlowText), s => s.IsResolveSlow);
        _form.Text(slow, s => s.DiscoverySlowText);

        var bar = new WinoInfoBar(WinoInfoBarSeverity.Warning);
        Bindings.Own(bar);
        _form.Apply(() =>
        {
            bar.Title = ViewModel.NotFoundTitle;
            bar.Message = ViewModel.NotFoundMessage;
        });
        var manual = _form.Button(Translator.AccountSetup_SetUpManually, vm.OpenAdvancedSetupCommand, suggested: true);
        _form.Apply(() => OnboardingUi.SetLabel(manual, ViewModel.NotFoundManualText is { Length: > 0 } text ? text : Translator.AccountSetup_SetUpManually));
        var retry = _form.Button(Translator.AccountSetup_Retry, vm.ResolveAgainCommand);
        var choose = OnboardingUi.Link(Translator.AccountSetup_ChooseProvider);
        Bindings.Own(GtkSignals.OnClicked(choose, ShowProviderPicker));
        _form.When(choose, s => s.IsChooseProviderVisible);
        bar.Content = OnboardingUi.Buttons(manual, retry, choose);
        var notFound = _form.When(OnboardingUi.Column(0, bar.Root), s => s.IsNotFoundVisible);

        return OnboardingUi.Column(12, slow, notFound);
    }
}
