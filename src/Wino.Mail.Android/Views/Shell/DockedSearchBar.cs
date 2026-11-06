using Android.Content;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using Fragment = AndroidX.Fragment.App.Fragment;
using Google.Android.Material.AppBar;
using Google.Android.Material.Shape;
using Wino.Core.Domain;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Shell;

/// <summary>
/// The docked search bar every mode shows at the top (canvas Mail inbox redesign, Material 3 docked search bar): a
/// 56 dp pill with the menu button that opens the navigation drawer (the accounts menu), the search field, the page's
/// action icons and its overflow. It turns a page's existing <see cref="MaterialToolbar"/> into the pill, so the page keeps
/// adding its actions to <see cref="MaterialToolbar.Menu"/>. Focusing the field (or <see cref="Activate"/>) puts the bar in
/// search: the menu button becomes Back, and Back (the button or the system gesture) clears the query and leaves search.
/// </summary>
internal sealed class DockedSearchBar
{
    private readonly MaterialToolbar _toolbar;
    private readonly Action _openNavigation;
    private readonly AndroidX.Activity.OnBackPressedCallback _back;
    private bool _active;
    private bool _settingText;
    private Action? _closeContextual;
    private readonly MaterialShapeDrawable _shape;

    private DockedSearchBar(MaterialToolbar toolbar, Fragment owner, string hint, Action openNavigation)
    {
        _toolbar = toolbar;
        _openNavigation = openNavigation;
        var context = toolbar.RequireContext();

        // The pill: the zone surface (custom themes recolour it) with its hairline, fully rounded.
        int height = WinoUnits.Dp(context, 56);
        var shape = _shape = new MaterialShapeDrawable(ShapeAppearanceModel.InvokeBuilder().SetAllCornerSizes(height / 2f).Build());
        shape.FillColor = global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.ZoneFill(context));
        shape.SetStroke(Math.Max(1, WinoUnits.Dp(context, 1)), WinoColors.ZoneStroke(context));
        toolbar.Background = shape;
        toolbar.Elevation = 0;
        toolbar.SetMinimumHeight(height);
        toolbar.SetPadding(WinoUnits.Dp(context, 4), 0, WinoUnits.Dp(context, 4), 0);
        toolbar.SetContentInsetsRelative(0, 0);
        toolbar.ContentInsetStartWithNavigation = 0;
        toolbar.Title = null;
        if (toolbar.LayoutParameters is ViewGroup.MarginLayoutParams margins)
        {
            margins.Height = height;
            int side = WinoUnits.Dp(context, 16);
            margins.SetMargins(side, WinoUnits.Dp(context, 12), side, WinoUnits.Dp(context, 8));
            toolbar.LayoutParameters = margins;
        }

        Field = new EditText(context)
        {
            Hint = hint,
            Background = null,
            ImeOptions = ImeAction.Search,
            InputType = InputTypes.ClassText | InputTypes.TextFlagNoSuggestions,
            ContentDescription = hint
        };
        Field.SetSingleLine(true);
        Field.SetPadding(WinoUnits.Dp(context, 4), 0, WinoUnits.Dp(context, 4), 0);
        WinoTokens.Style(Field, WinoTextStyle.BodyLarge);
        Field.SetTextColor(WinoColors.PrimaryText(context));
        Field.SetHintTextColor(WinoColors.SecondaryText(context));
        toolbar.AddView(Field, new AndroidX.AppCompat.Widget.Toolbar.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        Field.FocusChange += (_, args) => { if (args.HasFocus) SetActive(true); };
        Field.TextChanged += (_, _) => { if (!_settingText) QueryChanged?.Invoke(Query); };
        Field.EditorAction += (_, args) =>
        {
            args.Handled = args.ActionId == ImeAction.Search;
            if (!args.Handled) return;
            HideKeyboard();
            Submitted?.Invoke(Query);
        };

        toolbar.NavigationClick += (_, _) =>
        {
            if (_closeContextual is { } close) close();
            else if (_active) Deactivate();
            else _openNavigation();
        };

        _back = new BackCallback(this);
        if (owner.Activity is { } activity) activity.OnBackPressedDispatcher.AddCallback(owner.ViewLifecycleOwner, _back);
        ApplyNavigationIcon();
    }

    /// <summary>Turns <paramref name="toolbar"/> into the docked search bar of <paramref name="owner"/>'s page.</summary>
    public static DockedSearchBar Attach(MaterialToolbar toolbar, Fragment owner, string hint, Action openNavigation)
        => new(toolbar, owner, hint, openNavigation);

    public MaterialToolbar Toolbar => _toolbar;

    public EditText Field { get; }

    public string Hint
    {
        get => Field.Hint ?? string.Empty;
        set
        {
            Field.Hint = value;
            Field.ContentDescription = value;
        }
    }

    public string Query
    {
        get => Field.Text ?? string.Empty;
        set
        {
            if (Query == value) return;
            _settingText = true;
            try
            {
                Field.Text = value;
                Field.SetSelection(Field.Text?.Length ?? 0);
            }
            finally { _settingText = false; }
        }
    }

    public bool IsActive => _active;

    /// <summary>The typed text changed (not raised for <see cref="Query"/> set from code).</summary>
    public event Action<string>? QueryChanged;

    /// <summary>The keyboard's Search key.</summary>
    public event Action<string>? Submitted;

    /// <summary>The bar entered or left search.</summary>
    public event Action<bool>? ActiveChanged;

    /// <summary>Enters search; <paramref name="focus"/> also shows the keyboard.</summary>
    public void Activate(bool focus)
    {
        SetActive(true);
        if (!focus) return;
        Field.RequestFocus();
        (Field.Context?.GetSystemService(Context.InputMethodService) as InputMethodManager)?.ShowSoftInput(Field, ShowFlags.Implicit);
    }

    /// <summary>Leaves search: the query clears (raising <see cref="QueryChanged"/>) and the keyboard closes.</summary>
    public void Deactivate()
    {
        HideKeyboard();
        Field.ClearFocus();
        if (Query.Length > 0) Field.Text = string.Empty;
        SetActive(false);
    }

    public bool IsContextual => _closeContextual is not null;

    /// <summary>
    /// The contextual bar (canvas "selection mode"): the pill turns secondary container, Close replaces the menu button and
    /// <paramref name="title"/> replaces the field. The page sets the contextual actions on the menu. Back runs
    /// <paramref name="close"/>.
    /// </summary>
    public void BeginContextual(string title, Action close)
    {
        if (_active) Deactivate();
        _closeContextual = close;
        var context = _toolbar.RequireContext();
        Field.Visibility = ViewStates.Gone;
        HideKeyboard();
        _toolbar.Title = title;
        _toolbar.SetTitleTextColor(WinoColors.PrimaryText(context));
        _shape.FillColor = global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.SecondaryContainer(context));
        _back.Enabled = true;
        ApplyNavigationIcon();
    }

    public void SetContextualTitle(string title, string? description = null)
    {
        if (!IsContextual) return;
        _toolbar.Title = title;
        if (description is not null) _toolbar.ContentDescription = description;
    }

    /// <summary>Back to the search bar.</summary>
    public void EndContextual()
    {
        if (!IsContextual) return;
        _closeContextual = null;
        var context = _toolbar.RequireContext();
        _toolbar.Title = null;
        _toolbar.ContentDescription = null;
        Field.Visibility = ViewStates.Visible;
        _shape.FillColor = global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.ZoneFill(context));
        _back.Enabled = _active;
        ApplyNavigationIcon();
    }

    public void HideKeyboard()
        => (Field.Context?.GetSystemService(Context.InputMethodService) as InputMethodManager)?.HideSoftInputFromWindow(Field.WindowToken, HideSoftInputFlags.None);

    private void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        _back.Enabled = active;
        ApplyNavigationIcon();
        ActiveChanged?.Invoke(active);
    }

    private void ApplyNavigationIcon()
    {
        if (IsContextual)
        {
            _toolbar.SetNavigationIcon(Resource.Drawable.wino_ic_close);
            _toolbar.NavigationContentDescription = Translator.Buttons_Close;
            return;
        }
        _toolbar.SetNavigationIcon(_active ? Resource.Drawable.wino_ic_arrow_back : Resource.Drawable.wino_ic_menu);
        _toolbar.NavigationContentDescription = _active ? Translator.Buttons_Back : Translator.Android_Shell_OpenNavigation;
    }

    private sealed class BackCallback(DockedSearchBar owner) : AndroidX.Activity.OnBackPressedCallback(false)
    {
        public override void HandleOnBackPressed()
        {
            if (owner._closeContextual is { } close) close();
            else owner.Deactivate();
        }
    }
}
