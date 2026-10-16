using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.ViewModels;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Categories of one account (Mac MailCategoryManagementPageViewController, Windows MailCategoryManagementPage): the
/// page description, Refresh (Outlook only) and Add, then one card per category with its colour chip, name, colour
/// pair, a 44pt favourite toggle and an actions menu with Edit and Delete (inline buttons on the Mac). Navigation
/// parameter: the account id.
/// </summary>
public sealed class MailCategoryManagementPageViewController(MailCategoryManagementPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<MailCategoryManagementPageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private readonly WinoSettingsGroup _categories = new();
    private BindingScope? _rowScope;
    private bool _rebuildQueued;

    public string? PageTitle => Translator.MailCategoryManagementPage_Title;
    /// <summary>The title is fixed.</summary>
    public event EventHandler? PageTitleChanged { add { } remove { } }

    protected override void BuildPage()
    {
        var vm = ViewModel;
        var add = Bind.Button(Translator.Buttons_Add, vm.AddCategoryCommand, primary: true, icon: WinoIconGlyph.Add);
        var refresh = Bind.Button(Translator.Buttons_Refresh, vm.RefreshCategoriesCommand, icon: WinoIconGlyph.ArrowClockwise);
        WinoAccessibility.Help(refresh, Translator.MailCategoryManagementPage_RefreshConfirmationMessage);
        Bind.Visible(refresh, vm, nameof(vm.CanRefresh), s => s.CanRefresh);
        AddGroup(null, ActionRow(add), ActionRow(refresh));

        var empty = Card(Translator.MailCategoryManagementPage_Empty, null, WinoIconGlyph.Tag);
        Bind.Visible(Add(empty), vm, nameof(vm.HasCategories), s => !s.HasCategories);

        Add(_categories);
        // The ViewModel clears and refills the collection; coalesce those changes into one rebuild.
        Bind.Collection(vm.Categories, QueueRebuild);
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeAsync(mode, parameter);

    private void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        BeginInvokeOnMainThread(() =>
        {
            _rebuildQueued = false;
            if (!Bindings.IsDisposed) Rebuild();
        });
    }

    private void Rebuild()
    {
        _rowScope?.Dispose();
        _rowScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_rowScope);
        _categories.Clear();

        foreach (var category in ViewModel.Categories.ToList())
            _categories.Add(CategoryCard(rows, category));

        _categories.Hidden = _categories.RowCount == 0;
    }

    private WinoSettingsCard CategoryCard(SettingsBinder rows, MailCategory category)
    {
        var name = category.Name ?? string.Empty;
        var favorite = new SettingsGlyphButton(category.IsFavorite ? WinoIconGlyph.StarFilled : WinoIconGlyph.Star, ActionLabel(Translator.ContactEditor_Favorite, name))
        {
            IsToggled = category.IsFavorite
        };
        rows.OnActivated(favorite, () => Run(ViewModel.SetFavoriteAsync(category, !category.IsFavorite)));

        var edit = UIAction.Create(Translator.Buttons_Edit, WinoIcons.Image(WinoIconGlyph.Edit, 17), null, _ => Run(ViewModel.EditCategoryAsync(category)));
        var delete = UIAction.Create(Translator.Buttons_Delete, WinoIcons.Image(WinoIconGlyph.Delete, 17, WinoStyle.Critical), null, _ => Run(ViewModel.DeleteCategoryAsync(category)));
        delete.Attributes = UIMenuElementAttributes.Destructive;
        var more = new UIButton(UIButtonType.System) { TranslatesAutoresizingMaskIntoConstraints = false, ShowsMenuAsPrimaryAction = true };
        more.SetImage(WinoIcons.Image(WinoIconGlyph.More, 17, WinoStyle.SecondaryText), UIControlState.Normal);
        more.Menu = UIMenu.Create([edit, delete]);
        WinoLayout.Size(more, 44, 44);
        WinoAccessibility.Label(more, ActionLabel(Translator.More, name));

        var card = new WinoSettingsCard(name, $"{category.BackgroundColorHex} / {category.TextColorHex}", WinoIconGlyph.None, WinoLayout.HStack(0, favorite, more))
        {
            LeadingView = Chip(category)
        };
        card.AccessibilityLabel = category.IsFavorite ? $"{name}, {Translator.ContactEditor_Favorite}" : name;
        return card;
    }

    /// <summary>The Windows 28pt rounded chip: category background with its text colour as the border.</summary>
    private static UIView Chip(MailCategory category)
    {
        var chip = new WinoSurfaceView
        {
            Fill = WinoStyle.FromHexString(category.BackgroundColorHex) ?? WinoStyle.SubtleFill,
            Stroke = WinoStyle.FromHexString(category.TextColorHex) ?? WinoStyle.GroupStroke,
            StrokeWidth = 1,
            CornerRadius = 8,
        };
        WinoLayout.Size(chip, 28, 28);
        chip.IsAccessibilityElement = false;
        return chip;
    }

    /// <summary>Icon-only buttons carry the action and the category name as their accessible name.</summary>
    private static string ActionLabel(string action, string name) => string.IsNullOrEmpty(name) ? action : $"{action}: {name}";

    private async void Run(Task operation)
    {
        try { await operation; }
        catch (Exception exception) { ReportError(exception); }
    }
}
