using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// An account's mail categories (Windows MailCategoryManagementPage): Add, Refresh for providers that sync categories,
/// and per category its colour chip, favourite, Edit and Delete. The navigation parameter is the account id.
/// </summary>
internal sealed class MailCategoryManagementPage(MailCategoryManagementPageViewModel viewModel, WinoViewServices services)
    : SettingsPageView<MailCategoryManagementPageViewModel>(viewModel, services), ISettingsPageTitleSource
{
    private WinoSettingsGroup? _list;
    private Gtk.Button? _refresh;
    private SettingsBinder? _rows;

#if DEBUG
    internal static MailCategoryManagementPage? DebugCurrent { get; private set; }
#endif

    public string? PageTitle => Translator.MailCategoryManagementPage_Title;

    public string? PageDescription => ViewModel.Account is { } account ? account.Name ?? account.Address : null;

    public event EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        _list = Page.AddGroup(null, Translator.MailCategoryManagementPage_Description);
        _list.HidesWhenEmpty = false;
        var buttons = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        _refresh = Settings.Button(Translator.Buttons_Refresh, vm.RefreshCategoriesCommand);
        _refresh.SetTooltipText(Translator.MailCategoryManagementPage_RefreshConfirmationMessage);
        _refresh.SetVisible(false);
        buttons.Append(_refresh);
        buttons.Append(Settings.Button(Translator.Buttons_Add, vm.AddCategoryCommand, suggested: true));
        _list.HeaderSuffix = buttons;
        Settings.Bind(vm, nameof(vm.Account), static s => s.Account, _ =>
        {
            _refresh?.SetVisible(ViewModel.CanRefresh);
            PageTitleChanged?.Invoke(this, EventArgs.Empty);
        });
        Settings.Collection(vm.Categories, Rebuild);
#if DEBUG
        DebugCurrent = this;
#endif
    }

    protected override Task NavigateAsync(NavigationMode mode, object? parameter) => ViewModel.InitializeAsync(mode, parameter);

    protected override Task DeactivateAsync()
    {
#if DEBUG
        if (ReferenceEquals(DebugCurrent, this)) DebugCurrent = null;
#endif
        return base.DeactivateAsync();
    }

    private void Rebuild()
    {
        if (_list is null || IsReleased) return;
        _rows?.Scope.Dispose();
        _list.Clear();
        var rows = _rows = Settings.Child();
        if (ViewModel.Categories.Count == 0)
        {
            var empty = WinoSettingsRows.Action(Translator.MailCategoryManagementPage_Empty, null, WinoIconGlyph.Tag);
            empty.AddCssClass("dim-label");
            _list.Add(empty);
            return;
        }
        foreach (var category in ViewModel.Categories.ToList())
        {
            if (category is not null) _list.Add(Row(rows, category));
        }
    }

    private Adw.ActionRow Row(SettingsBinder rows, MailCategory category)
    {
        var name = category.Name ?? string.Empty;
        var background = category.BackgroundColorHex ?? string.Empty;
        var foreground = category.TextColorHex ?? string.Empty;
        var row = WinoSettingsRows.Action(name, $"{background} / {foreground}");
        row.SetTitleLines(1);

        var chip = Adw.Bin.New();
        chip.SetSizeRequest(28, 28);
        chip.SetValign(Gtk.Align.Center);
        chip.AddCssClass("wino-category-chip");
        if (WinoTint.FillClassFor(background, foreground) is { } fill) chip.AddCssClass(fill);
        else chip.AddCssClass("wino-category-chip-empty");
        row.AddPrefix(chip);

        var favorite = WinoIcons.ToggleButton(category.IsFavorite ? WinoIconGlyph.StarFilled : WinoIconGlyph.Star, $"{Translator.ContactEditor_Favorite}: {name}");
        favorite.SetValign(Gtk.Align.Center);
        favorite.SetActive(category.IsFavorite);
        if (category.IsFavorite) favorite.AddCssClass("warning");
        WinoSettingsRows.Suffix(row, favorite);
        rows.Own(GtkSignals.OnToggled(favorite, () =>
        {
            if (favorite.GetActive() == category.IsFavorite) return;
            Observe(ViewModel.SetFavoriteAsync(category, favorite.GetActive()));
        }));

        var edit = WinoSettingsRows.Suffix(row, WinoSettingsRows.GlyphButton(WinoIconGlyph.Edit, $"{Translator.Buttons_Edit}: {name}"));
        rows.Own(GtkSignals.OnClicked(edit, () => Observe(ViewModel.EditCategoryAsync(category))));
        var delete = WinoSettingsRows.Suffix(row, WinoSettingsRows.GlyphButton(WinoIconGlyph.Delete, $"{Translator.Buttons_Delete}: {name}"));
        rows.Own(GtkSignals.OnClicked(delete, () => Observe(ViewModel.DeleteCategoryAsync(category))));

        WinoA11y.Rename(row, category.IsFavorite ? $"{name}, {Translator.ContactEditor_Favorite}" : name);
        return row;
    }

#if DEBUG
    internal string DebugDescribe()
        => string.Join('\n', ViewModel.Categories.Select(category => $"{category.Name}|{category.BackgroundColorHex}|{category.TextColorHex}|{category.IsFavorite}|{category.Source}"))
           + $"\nrefresh={_refresh?.GetVisible()} count={ViewModel.Categories.Count}\n";
#endif
}
