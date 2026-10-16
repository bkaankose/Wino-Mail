using Android.Views;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.ViewModels;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;
using ActionDisposable = Wino.Mail.Android.Infrastructure.ActionDisposable;

namespace Wino.Mail.Android.Views.Settings.Accounts;

/// <summary>
/// Categories of one account (iOS MailCategoryManagementPageViewController, Windows MailCategoryManagementPage): the
/// page description, Add and Refresh (Outlook only), then one card per category with its colour chip, name, colour pair,
/// a 48 dp favourite toggle and an actions menu with Edit and Delete. The add and edit forms are the ViewModel's dialogs.
/// Navigation parameter: the account id.
/// </summary>
public sealed class MailCategoryManagementPageFragment : SettingsPageFragment<MailCategoryManagementPageViewModel>, ISettingsPageTitleSource
{
    private WinoSettingsGroup? _categories;
    private BindingScope? _rowScope;
    private bool _rebuildQueued;

    public string? PageTitle => Translator.MailCategoryManagementPage_Title;

    /// <summary>The page shows its own description, so the catalogue line is not repeated.</summary>
    protected override bool ShowsPageDescription => false;

    protected override void BuildPage()
    {
        var vm = VM;
        Bindings.Own(new ActionDisposable(() => _rowScope?.Dispose()));
        AddIntro(Translator.MailCategoryManagementPage_Description);

        var refresh = ActionCard(Translator.Buttons_Refresh, vm.RefreshCategoriesCommand);
        Bind.Visible(refresh, vm, nameof(vm.CanRefresh), s => s.CanRefresh);
        AddGroup(null, ActionCard(Translator.Buttons_Add, vm.AddCategoryCommand), refresh);

        var empty = Card(Translator.MailCategoryManagementPage_Empty, null, WinoIconGlyph.Tag);
        Bind.Visible(Add(empty), vm, nameof(vm.HasCategories), s => !s.HasCategories);

        _categories = Add(Group(null));
        // The ViewModel clears and refills the collection; coalesce those changes into one rebuild.
        Bind.Collection(vm.Categories, QueueRebuild);
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
        if (_categories is null) return;
        _rowScope?.Dispose();
        _rowScope = new BindingScope();
        var rows = Bind.Child(_rowScope);
        _categories.Clear();

        foreach (var category in VM.Categories.ToList())
            _categories.Add(CategoryCard(rows, category));

        _categories.Visibility = _categories.RowCount == 0 ? ViewStates.Gone : ViewStates.Visible;
    }

    private WinoSettingsCard CategoryCard(SettingsBinder rows, MailCategory category)
    {
        var name = category.Name ?? string.Empty;
        var favorite = AccountSettingsParts.GlyphButton(Ctx, category.IsFavorite ? WinoIconGlyph.StarFilled : WinoIconGlyph.Star,
            AccountSettingsParts.ActionLabel(Translator.ContactEditor_Favorite, name), category.IsFavorite);
        rows.Views.Click(favorite, () => AccountSettingsParts.Run(VM.SetFavoriteAsync(category, !category.IsFavorite), ReportError));

        var more = AccountSettingsParts.MoreButton(Ctx, rows, AccountSettingsParts.ActionLabel(Translator.More, name),
        [
            new(Translator.Buttons_Edit, () => AccountSettingsParts.Run(VM.EditCategoryAsync(category), ReportError)),
            new(Translator.Buttons_Delete, () => AccountSettingsParts.Run(VM.DeleteCategoryAsync(category), ReportError), IsDestructive: true)
        ]);

        var actions = AccountSettingsParts.HRow(Ctx);
        actions.AddView(favorite);
        actions.AddView(more);
        var card = new WinoSettingsCard(Ctx, name, $"{category.BackgroundColorHex} / {category.TextColorHex}", WinoIconGlyph.None, actions)
        {
            LeadingView = Chip(category)
        };
        card.ContentDescription = category.IsFavorite ? $"{name}, {Translator.ContactEditor_Favorite}" : name;
        return card;
    }

    /// <summary>The Windows 28 dp rounded chip: category background with its text colour as the border.</summary>
    private View Chip(MailCategory category)
    {
        var size = WinoTokens.Px(Ctx, Resource.Dimension.wino_size_swatch);
        var chip = new View(Ctx)
        {
            Background = AccountSettingsParts.Surface(Ctx,
                WinoColors.FromHexString(category.BackgroundColorHex) ?? WinoColors.SubtleFill(Ctx),
                WinoColors.FromHexString(category.TextColorHex) ?? WinoColors.CardStroke(Ctx), Resource.Dimension.wino_radius_large),
            ImportantForAccessibility = ImportantForAccessibility.No,
            LayoutParameters = new global::Android.Widget.FrameLayout.LayoutParams(size, size)
        };
        return chip;
    }
}
