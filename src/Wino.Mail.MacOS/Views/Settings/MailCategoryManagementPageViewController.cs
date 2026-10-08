using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Categories of one account (Manage accounts › account › Categories; Windows MailCategoryManagementPage):
/// the page description, Refresh (Outlook only) and Add, then one card per category with its colour chip,
/// name, colour pair, a favourite toggle, Edit and Delete. Navigation parameter: the account id.
/// </summary>
public sealed class MailCategoryManagementPageViewController(MailCategoryManagementPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<MailCategoryManagementPageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private readonly WinoSettingsGroup _categories = new();
    private BindingScope? _rowScope;
    private bool _rebuildQueued;

    public string? PageTitle => Translator.MailCategoryManagementPage_Title;
    public event EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        AddIntro(Translator.MailCategoryManagementPage_Description);

        var add = Bind.Button(Translator.Buttons_Add, vm.AddCategoryCommand, primary: true, icon: WinoIconGlyph.Add);
        var refresh = Bind.Button(Translator.Buttons_Refresh, vm.RefreshCategoriesCommand, icon: WinoIconGlyph.ArrowClockwise);
        refresh.ToolTip = Translator.MailCategoryManagementPage_RefreshConfirmationMessage;
        Bind.Visible(refresh, vm, nameof(vm.CanRefresh), s => s.CanRefresh);
        var buttons = Row(add, refresh);
        buttons.EdgeInsets = new NSEdgeInsets(4, 0, 8, 0);
        Add(buttons);

        var empty = Card(Translator.MailCategoryManagementPage_Empty, null, WinoIconGlyph.Tag);
        Bind.Visible(Add(empty), vm, nameof(vm.HasCategories), s => !s.HasCategories);

        Add(_categories);
        // The ViewModel clears and refills the collection; coalesce those changes into one rebuild.
        Bind.Collection(vm.Categories, QueueRebuild);
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeAsync(mode, parameter!);

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
        var favorite = SettingsBinder.CreateButton(string.Empty, icon: category.IsFavorite ? WinoIconGlyph.StarFilled : WinoIconGlyph.Star);
        favorite.SetButtonType(NSButtonType.PushOnPushOff);
        favorite.State = category.IsFavorite ? NSCellStateValue.On : NSCellStateValue.Off;
        favorite.ContentTintColor = category.IsFavorite ? WinoStyle.Caution : null;
        Label(favorite, Translator.ContactEditor_Favorite, name);
        rows.OnActivated(favorite, () => Run(ViewModel.SetFavoriteAsync(category, favorite.State == NSCellStateValue.On)));

        var edit = rows.Button(string.Empty, () => Run(ViewModel.EditCategoryAsync(category)), icon: WinoIconGlyph.Edit);
        Label(edit, Translator.Buttons_Edit, name);
        var delete = rows.Button(string.Empty, () => Run(ViewModel.DeleteCategoryAsync(category)), icon: WinoIconGlyph.Delete);
        Label(delete, Translator.Buttons_Delete, name);

        var card = new WinoSettingsCard(name, $"{category.BackgroundColorHex} / {category.TextColorHex}", WinoIconGlyph.None, Row(favorite, edit, delete))
        {
            LeadingView = Chip(category)
        };
        card.AccessibilityLabel = category.IsFavorite ? $"{name}, {Translator.ContactEditor_Favorite}" : name;
        return card;
    }

    /// <summary>The Windows 28pt rounded chip: category background with its text colour as the border.</summary>
    private static NSView Chip(MailCategory category)
    {
        var chip = new WinoSurfaceView
        {
            Fill = WinoStyle.FromHexString(category.BackgroundColorHex) ?? WinoStyle.SubtleFill,
            Stroke = WinoStyle.FromHexString(category.TextColorHex) ?? WinoStyle.GroupStroke,
            StrokeWidth = 1,
            CornerRadius = 8,
        };
        WinoLayout.Size(chip, 28, 28);
        chip.AccessibilityElement = false;
        return chip;
    }

    /// <summary>Icon-only buttons carry the action and the category name, as tooltip and accessible name.</summary>
    private static void Label(NSButton button, string action, string name)
    {
        var text = string.IsNullOrEmpty(name) ? action : $"{action}: {name}";
        button.ToolTip = text;
        WinoAccessibility.Label(button, text);
    }

    private async void Run(Task operation)
    {
        try { await operation; }
        catch (Exception exception) { ReportError(exception); }
    }
}
