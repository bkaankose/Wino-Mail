using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.Graphics.Drawable;
using Google.Android.Material.AppBar;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Settings;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Settings;
using Wino.Presentation.Android;
using FragmentManager = AndroidX.Fragment.App.FragmentManager;
using FragmentTransaction = AndroidX.Fragment.App.FragmentTransaction;

namespace Wino.Mail.Android.Views.Settings;

/// <summary>Implemented by Settings pages whose title follows their content (an account name).</summary>
public interface ISettingsPageTitleSource
{
    /// <summary>The page title; null or empty keeps the catalogue title.</summary>
    string? PageTitle { get; }
}

/// <summary>
/// The Settings surface over the shell (plan section 1: Settings is never a mode; canvas boards Settings and
/// SettingsMessageList). It shows the navigation service's Settings stack: on compact widths the top page (the root list
/// first) under a 64 dp Material 3 top app bar with Back and the page title; on medium and expanded widths the
/// root list in its own zone beside the page, like the iOS and Mac Settings and the Windows settings pane, with the
/// Windows default page (General) selected when nothing else is. Back pops the stack; from a root row's page on a wide
/// window it closes Settings (<see cref="AndroidNavigationService"/>).
/// </summary>
public sealed class SettingsHostFragment : WinoFragment
{
    private AndroidNavigationService? _navigation;
    private MaterialToolbar? _toolbar;
    private TextView? _detailTitle;
    private bool _twoPane;
    private bool _selectingDefault;
    private IMenuItem? _searchItem;

    protected override int LayoutResource => Resource.Layout.fragment_settings_host;

    protected override void OnBind(View view)
    {
        _navigation = MainApplication.Services?.GetService<AndroidNavigationService>();
        if (_navigation is null) return;
        var context = view.RequireContext();
        _twoPane = view.FindViewById(Resource.Id.settings_list_host) is not null;
        WinoInsets.Apply(view.FindViewById(Resource.Id.settings_bar) ?? view, top: true, bottom: false);
        // Forms in Settings stay above the keyboard.
        WinoInsets.ApplyWithKeyboard(view.FindViewById(Resource.Id.settings_root) ?? view, top: false);

        _toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.settings_toolbar);
        if (_toolbar is not null)
        {
            _toolbar.NavigationIcon = new DrawerArrowDrawable(context) { Color = WinoColors.PrimaryText(context), Progress = 1 };
            _toolbar.NavigationContentDescription = Translator.Buttons_Back;
            EventHandler<AndroidX.AppCompat.Widget.Toolbar.NavigationClickEventArgs> back = (_, _) => CloseOrBack(fromToolbar: true);
            _toolbar.NavigationClick += back;
            _searchItem = _toolbar.Menu?.Add(0, 1, 0, Translator.SearchBarPlaceholder);
            if (_searchItem is not null)
            {
                _searchItem.SetIcon(WinoIcons.Drawable(context, WinoIconGlyph.Find, WinoTokens.Dp(context, Resource.Dimension.wino_glyph_xl), WinoColors.PrimaryText(context)));
                _searchItem.SetShowAsAction(ShowAsAction.Always);
            }
            EventHandler<AndroidX.AppCompat.Widget.Toolbar.MenuItemClickEventArgs> menu = (_, _) => RootList()?.ToggleSearch();
            _toolbar.MenuItemClick += menu;
            Bindings.Own(new ActionDisposable(() =>
            {
                _toolbar.NavigationClick -= back;
                _toolbar.MenuItemClick -= menu;
            }));
        }

        _detailTitle = view.FindViewById<TextView>(Resource.Id.settings_detail_title);
        if (view.FindViewById<ImageButton>(Resource.Id.settings_detail_back) is { } detailBack)
        {
            detailBack.SetImageDrawable(new DrawerArrowDrawable(context) { Color = WinoColors.PrimaryText(context), Progress = 1 });
            detailBack.ContentDescription = Translator.Buttons_Back;
            EventHandler click = (_, _) => CloseOrBack(fromToolbar: false);
            detailBack.Click += click;
            Bindings.Own(new ActionDisposable(() => detailBack.Click -= click));
        }

        WinoSettingsNavigation.Presenter ??= OpenDetail;
        _navigation.SettingsPagesChanged += PagesChanged;
        Bindings.Own(new ActionDisposable(() => _navigation.SettingsPagesChanged -= PagesChanged));
        PlacePages();
    }

    /// <summary>The toolbar's arrow closes Settings on a wide window (the list is the first page); otherwise it is Back.</summary>
    private void CloseOrBack(bool fromToolbar)
    {
        if (fromToolbar && _twoPane && _navigation is not null)
        {
            _ = ((Wino.Mail.ViewModels.Navigation.INavigationHost)_navigation).CloseSettingsAsync();
            return;
        }
        Activity?.OnBackPressedDispatcher.OnBackPressed();
    }

    private void PagesChanged(object? sender, EventArgs args) => PlacePages();

    /// <summary>Puts the Settings stack in the panes. Main thread.</summary>
    private void PlacePages()
    {
        if (_navigation is null || View is null || !IsAdded) return;
        var manager = ChildFragmentManager;
        if (manager.IsDestroyed || manager.IsStateSaved) return;
        var entries = _navigation.SettingsEntries;
        if (entries.Count == 0) return;

        if (_twoPane)
        {
            Place(manager, Resource.Id.settings_list_host, entries[0]);
            if (entries.Count == 1)
            {
                // A wide window always shows a page beside the list: the Windows default (General).
                if (!_selectingDefault)
                {
                    _selectingDefault = true;
                    _ = SelectDefaultAsync();
                }
                return;
            }
            PlaceChain(manager, Resource.Id.settings_detail_host, entries[^1]);
        }
        else
        {
            PlaceChain(manager, Resource.Id.settings_detail_host, entries[^1]);
        }
        UpdateChrome();
    }

    private async Task SelectDefaultAsync()
    {
        try
        {
            if (_navigation is not null) await _navigation.SelectSettingsRootPageAsync(SettingsCatalog.DefaultPage);
        }
        catch (Exception exception) { ReportError(exception); }
        finally { _selectingDefault = false; }
    }

    private static void Place(FragmentManager manager, int containerId, PageEntry entry)
    {
        var current = manager.FindFragmentById(containerId);
        if (current is WinoFragment shown && ReferenceEquals(shown.Entry, entry)) return;
        manager.BeginTransaction()
            .SetReorderingAllowed(true)
            .SetTransition(current is null ? FragmentTransaction.TransitNone : FragmentTransaction.TransitFragmentFade)
            .Replace(containerId, entry.CreateFragment())
            .CommitNowAllowingStateLoss();
    }

    /// <summary>
    /// Shows <paramref name="top"/> in a pane. A setting detail page keeps the page it was opened from under it, hidden
    /// but bound, because the detail hosts that page's live controls; the page is placed (and bound) before the detail.
    /// </summary>
    private static void PlaceChain(FragmentManager manager, int containerId, PageEntry top)
    {
        var owner = WinoSettingDetailFragment.OwnerOf(top);
        var chain = owner is null ? new List<PageEntry> { top } : [owner, top];
        var shown = manager.Fragments.OfType<WinoFragment>().Where(fragment => fragment.Id == containerId).ToList();
        if (shown.Count == chain.Count && shown.Zip(chain).All(pair => ReferenceEquals(pair.First.Entry, pair.Second)) && !shown[^1].IsHidden) return;

        // Pass 1: the page (the owner, or the top page itself); anything else leaves the pane.
        var basePage = chain[0];
        var transaction = manager.BeginTransaction().SetReorderingAllowed(true);
        bool hadContent = shown.Count > 0;
        WinoFragment? baseFragment = null;
        foreach (var fragment in manager.Fragments.Where(fragment => fragment.Id == containerId).ToList())
        {
            if (fragment is WinoFragment wino && ReferenceEquals(wino.Entry, basePage) && baseFragment is null) baseFragment = wino;
            else transaction.Remove(fragment);
        }
        if (baseFragment is null)
        {
            baseFragment = basePage.CreateFragment();
            transaction.SetTransition(hadContent ? FragmentTransaction.TransitFragmentFade : FragmentTransaction.TransitNone);
            transaction.Add(containerId, baseFragment);
        }
        if (chain.Count > 1) transaction.Hide(baseFragment);
        else transaction.Show(baseFragment);
        transaction.CommitNowAllowingStateLoss();

        // Pass 2: the detail over its page.
        if (chain.Count > 1)
        {
            manager.BeginTransaction()
                .SetReorderingAllowed(true)
                .SetTransition(FragmentTransaction.TransitFragmentOpen)
                .Add(containerId, top.CreateFragment())
                .CommitNowAllowingStateLoss();
        }
    }

    /// <summary>
    /// A row asked for the detail page of <paramref name="source"/> (WinoSettingsNavigation): the detail is pushed on the
    /// Settings stack, addressed by its path from the page that owns the controls.
    /// </summary>
    private static void OpenDetail(View origin, View source)
    {
        if (MainApplication.Services?.GetService<AndroidNavigationService>() is not { } navigation) return;
        var entries = navigation.SettingsEntries;
        if (entries.Count == 0) return;
        var top = entries[^1];
        PageEntry owner;
        List<string> path;
        IReadOnlyList<View> sources;
        if (top.Fragment is WinoSettingDetailFragment { Source: { } current } detail && WinoSettingDetailFragment.OwnerOf(top) is { } detailOwner)
        {
            owner = detailOwner;
            path = [.. detail.Path];
            sources = WinoSettingsNavigation.ChildSources(current);
        }
        else if (top.Fragment is ISettingsDetailOwner page)
        {
            owner = top;
            path = [];
            sources = page.SettingSources();
        }
        else return;
        if (WinoSettingsNavigation.Segment(sources, source) is not { } segment) return;
        path.Add(segment);
        _ = navigation.PushSettingsDetailAsync(owner, [.. path]);
    }

    /// <summary>The root list fragment, wherever it is shown.</summary>
    private SettingsRootFragment? RootList()
    {
        if (View is null || !IsAdded) return null;
        var manager = ChildFragmentManager;
        return (manager.FindFragmentById(Resource.Id.settings_list_host) ?? manager.FindFragmentById(Resource.Id.settings_detail_host)) as SettingsRootFragment;
    }

    /// <summary>Re-reads the shown page's title (pages whose title follows their content call this).</summary>
    public void UpdateChrome()
    {
        if (_navigation is null || View is null) return;
        var entries = _navigation.SettingsEntries;
        if (entries.Count == 0) return;
        var top = entries[^1];
        var detail = top.Fragment ?? ChildFragmentManager.FindFragmentById(Resource.Id.settings_detail_host);
        var title = (detail as ISettingsPageTitleSource)?.PageTitle is { Length: > 0 } custom ? custom : SettingsCatalog.Title(top.Page);
        bool rootShown = top.Page == WinoPage.SettingsPage || _twoPane;
        // Pages are sections of rows inside the zone, like the root list (canvas Android settings boards).
        if (View.FindViewById<WinoZoneLayout>(Resource.Id.settings_detail_zone) is { } detailZone)
            detailZone.IsPlain = false;

        if (_toolbar is not null)
        {
            _toolbar.SetHeading(_twoPane || top.Page == WinoPage.SettingsPage ? Translator.MenuSettings : title);
            _searchItem?.SetVisible(rootShown);
        }
        if (_detailTitle is not null) _detailTitle.Text = title;
        // The page's own back arrow only when something is under it besides the list.
        if (View.FindViewById(Resource.Id.settings_detail_back) is { } detailBack)
            detailBack.Visibility = entries.Count > 2 ? ViewStates.Visible : ViewStates.Gone;
        if (View.FindViewById(Resource.Id.settings_detail_header) is { } header)
            header.SetPadding(entries.Count > 2 ? WinoTokens.Px(header.RequireContext(), Resource.Dimension.wino_space_2xs) : WinoTokens.Px(header.RequireContext(), Resource.Dimension.wino_space_l),
                header.PaddingTop, header.PaddingRight, header.PaddingBottom);
    }
}

/// <summary>A vertical column at most 836 dp wide (Windows settings MaxWidth 900 less its padding), centred by its parent.</summary>
[global::Android.Runtime.Register("com.winomail.mail.android.SettingsColumn")]
public sealed class SettingsColumn : LinearLayout
{
    public SettingsColumn(Context context) : base(context) => Orientation = Orientation.Vertical;
    public SettingsColumn(Context context, global::Android.Util.IAttributeSet? attrs) : base(context, attrs) { }
    private SettingsColumn(IntPtr handle, global::Android.Runtime.JniHandleOwnership transfer) : base(handle, transfer) { }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int max = WinoTokens.Px(Context ?? throw new InvalidOperationException("The column has no context."), Resource.Dimension.wino_settings_column_max);
        if (MeasureSpec.GetSize(widthMeasureSpec) > max)
            widthMeasureSpec = MeasureSpec.MakeMeasureSpec(max, MeasureSpecMode.Exactly);
        base.OnMeasure(widthMeasureSpec, heightMeasureSpec);
    }
}
