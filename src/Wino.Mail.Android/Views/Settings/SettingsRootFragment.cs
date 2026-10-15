using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Personalization;
using Wino.Core.Domain.Models.Settings;
using Wino.Core.ViewModels;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Settings;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.UI;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Settings;

/// <summary>
/// The Settings root list (canvas boards Settings and SettingsDark; iOS SettingsRootViewController, the Mac sidebar and
/// the Windows settings pane): the shared <see cref="SettingsCatalog"/> rows in the Windows groups, names and order with
/// colourful glyphs, a trailing value where Windows shows one (theme, account count, version) and a chevron. Settings
/// search (the app bar's search action) filters the rows with the shared ranking. A row opens its page over the list on
/// compact widths and beside it on wider ones, where the open page's row stays highlighted.
/// </summary>
public sealed class SettingsRootFragment : SettingsPageFragment<SettingOptionsPageViewModel>,
    IRecipient<LanguageChanged>, IRecipient<WinoIntelligenceEntitlementChanged>
{
    private readonly Dictionary<WinoPage, View> _rows = new();
    private LinearLayout? _list;
    private Google.Android.Material.TextField.TextInputLayout? _search;
    private AndroidNavigationService? _navigation;
    private bool? _lastCanAccessIntelligence;

    protected override bool ShowsPageDescription => false;

    protected override void BuildPage()
    {
        _navigation = MainApplication.Services?.GetService<AndroidNavigationService>();
        WinoTokens.Pad(Page, 0, Resource.Dimension.wino_settings_page_padding_top, 0, Resource.Dimension.wino_settings_page_padding_bottom);

        _search = SettingsBinder.CreateTextField(Ctx, Translator.SearchBarPlaceholder);
        _search.Visibility = ViewStates.Gone;
        _search.SetPadding(WinoTokens.Px(Ctx, Resource.Dimension.wino_space_s), 0, WinoTokens.Px(Ctx, Resource.Dimension.wino_space_s), WinoTokens.Px(Ctx, Resource.Dimension.wino_space_2xs));
        _search.StartIconDrawable = WinoIcons.Drawable(Ctx, WinoIconGlyph.Find, WinoTokens.Dp(Ctx, Resource.Dimension.wino_glyph_ml), WinoColors.SecondaryText(Ctx));
        Add(_search);
        if (_search.EditText is { } field)
        {
            field.ImeOptions = ImeAction.Search;
            EventHandler<global::Android.Text.TextChangedEventArgs> changed = (_, _) => Rebuild();
            field.TextChanged += changed;
            Bindings.Own(new ActionDisposable(() => field.TextChanged -= changed));
        }

        _list = new LinearLayout(Ctx) { Orientation = Orientation.Vertical };
        // The rows run edge to edge (a Material 3 list).
        Page.AddView(_list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        Bind.Bind(VM, nameof(VM.AccountCount), vm => vm.AccountCount, _ => Rebuild());
        Bind.Bind(VM, nameof(VM.VersionText), vm => vm.VersionText, _ => Rebuild());
        if (_navigation is not null)
        {
            _navigation.SettingsPagesChanged += PagesChanged;
            Bindings.Own(new ActionDisposable(() => _navigation.SettingsPagesChanged -= PagesChanged));
        }
        WeakReferenceMessenger.Default.Register<LanguageChanged>(this);
        WeakReferenceMessenger.Default.Register<WinoIntelligenceEntitlementChanged>(this);
        Bindings.Own(new ActionDisposable(() => WeakReferenceMessenger.Default.UnregisterAll(this)));
    }

    /// <summary>Shows or hides the search field (the host's search action).</summary>
    public void ToggleSearch()
    {
        if (_search?.EditText is not { } field) return;
        bool show = _search.Visibility != ViewStates.Visible;
        _search.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
        var keyboard = Ctx.GetSystemService(Context.InputMethodService) as InputMethodManager;
        if (show)
        {
            field.RequestFocus();
            keyboard?.ShowSoftInput(field, ShowFlags.Implicit);
        }
        else
        {
            field.Text = string.Empty;
            keyboard?.HideSoftInputFromWindow(field.WindowToken, HideSoftInputFlags.None);
        }
    }

    private void Rebuild()
    {
        if (_list is null) return;
        _list.RemoveAllViews();
        _rows.Clear();
        _lastCanAccessIntelligence = SettingsCatalog.CanAccessIntelligence;
        var query = _search?.EditText?.Text?.Trim();
        if (!string.IsNullOrEmpty(query))
        {
            var results = SettingsCatalog.Search(query);
            if (results.Count == 0) _list.AddView(Header(Translator.ContextFlyout_NoResults));
            foreach (var entry in results) _list.AddView(RowFor(entry, showDescription: true));
        }
        else
        {
            foreach (var section in SettingsCatalog.GetSections())
            {
                if (section.Title is { } title) _list.AddView(Header(title));
                foreach (var entry in section.Entries) _list.AddView(RowFor(entry, showDescription: false));
            }
        }
        Highlight();
    }

    private View Header(string title)
    {
        // Material 3 list subheader in the primary colour.
        var header = Text(title, WinoTextStyle.Subheader);
        header.SetTextColor(WinoColors.AccentText(Ctx));
        // Aligned with the row text (72 dp), 48 dp tall with the label at its foot (canvas Settings).
        WinoTokens.Pad(header, Resource.Dimension.wino_m3_settings_text_inset, 0,
            Resource.Dimension.wino_settings_subheader_horizontal, Resource.Dimension.wino_m3_settings_subheader_bottom);
        header.Gravity = GravityFlags.Bottom | GravityFlags.Start;
        header.SetMinHeight(WinoTokens.Px(Ctx, Resource.Dimension.wino_m3_settings_subheader_height));
        AndroidX.Core.View.ViewCompat.SetAccessibilityHeading(header, true);
        return header;
    }

    /// <summary>A 56 dp Material 3 list item (canvas Settings): colourful glyph in a 40 dp slot, title (and the description in search results), value, chevron.</summary>
    private View RowFor(SettingsCatalogEntry entry, bool showDescription)
    {
        var context = Ctx;
        var row = new LinearLayout(context) { Orientation = Orientation.Horizontal, Clickable = true, Focusable = true };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetMinimumHeight(WinoTokens.Px(context, Resource.Dimension.wino_m3_list_item_one_line));
        row.SetPadding(WinoTokens.Px(context, Resource.Dimension.wino_space_l), WinoTokens.Px(context, Resource.Dimension.wino_space_xs), WinoTokens.Px(context, Resource.Dimension.wino_space_m), WinoTokens.Px(context, Resource.Dimension.wino_space_xs));
        var mask = new GradientDrawable();
        mask.SetColor(WinoTokens.Color(context, Resource.Color.wino_ripple_mask));
        mask.SetCornerRadius(WinoTokens.PxF(context, Resource.Dimension.wino_radius_small));
        row.Background = new RippleDrawable(global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.SubtleFill(context)), null, mask);

        var glyph = new WinoIconView(context) { Icon = entry.Icon, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_m3_settings_leading_glyph), Colorful = true, Gravity = GravityFlags.Center };
        glyph.SetTextColor(WinoColors.Accent(context));
        row.AddView(glyph, new LinearLayout.LayoutParams(WinoTokens.Px(context, Resource.Dimension.wino_m3_settings_leading_slot), WinoTokens.Px(context, Resource.Dimension.wino_m3_settings_leading_slot)));

        var texts = new LinearLayout(context) { Orientation = Orientation.Vertical };
        texts.AddView(Text(entry.Title, WinoTextStyle.ItemTitle, maxLines: 1));
        if (showDescription && !string.IsNullOrWhiteSpace(entry.Description)) texts.AddView(Text(entry.Description, WinoTextStyle.ItemSupporting, maxLines: 2));
        var textLayout = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1) { MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_space_l) };
        row.AddView(texts, textLayout);

        if (entry.Page is { } page && Value(page) is { Length: > 0 } value)
        {
            var label = Text(value, WinoTextStyle.Description, maxLines: 1);
            row.AddView(label, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_space_s) });
        }
        var chevron = new WinoIconView(context) { Icon = WinoIconGlyph.ChevronRight, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_xs) };
        chevron.SetTextColor(WinoColors.SecondaryText(context));
        row.AddView(chevron, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_space_m) });
        row.ContentDescription = entry.Title;

        if (entry.Page is { } target)
        {
            _rows[target] = row;
            Bind.Views.Click(row, () => Open(target));
        }
        return row;
    }

    /// <summary>The trailing value Windows and the canvas show on a few rows.</summary>
    private string? Value(WinoPage page) => page switch
    {
        WinoPage.ManageAccountsPage when VM.AccountCount > 0 => VM.AccountCount.ToString(System.Globalization.CultureInfo.CurrentCulture),
        WinoPage.AboutPage => VM.VersionText,
        WinoPage.PersonalizationPage => ThemeName(),
        _ => null
    };

    private static string? ThemeName()
    {
        var theme = MainApplication.Services?.GetService<INewThemeService>();
        if (theme?.CurrentApplicationThemeId is not { } id || id == WinoThemeCatalog.DefaultThemeId) return null;
        return WinoThemeCatalog.Find(id)?.Name;
    }

    private void Open(WinoPage page)
    {
        if (_navigation is { ShowsSettingsDetailPane: true } navigation) _ = navigation.SelectSettingsRootPageAsync(page);
        else Navigate(page);
    }

    private void PagesChanged(object? sender, EventArgs args) => Highlight();

    /// <summary>Marks the open page's row while the list is beside the page.</summary>
    private void Highlight()
    {
        if (_navigation is null || View is null) return;
        var entries = _navigation.SettingsEntries;
        WinoPage? open = _navigation.ShowsSettingsDetailPane && entries.Count > 1 ? SettingsCatalog.RootPage(entries[1].Page) : null;
        foreach (var (page, row) in _rows)
        {
            bool selected = page == open;
            row.Selected = selected;
            row.SetBackgroundColor(selected ? WinoColors.SelectedFill(Ctx) : Color.Transparent);
            if (!selected)
            {
                var mask = new GradientDrawable();
                mask.SetColor(WinoTokens.Color(Ctx, Resource.Color.wino_ripple_mask));
                mask.SetCornerRadius(WinoTokens.PxF(Ctx, Resource.Dimension.wino_radius_small));
                row.Background = new RippleDrawable(global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.SubtleFill(Ctx)), null, mask);
            }
        }
    }

    public void Receive(LanguageChanged message) => _ = Dispatcher.ExecuteOnUIThread(() =>
    {
        if (View is null) return;
        (ParentFragment as SettingsHostFragment)?.UpdateChrome();
        Rebuild();
    });

    /// <summary>Only an access change affects the list (every snapshot refresh republishes the entitlement).</summary>
    public void Receive(WinoIntelligenceEntitlementChanged message) => _ = Dispatcher.ExecuteOnUIThread(() =>
    {
        if (View is null || _lastCanAccessIntelligence == message.Entitlement.CanAccessSurfaces) return;
        Rebuild();
    });
}
