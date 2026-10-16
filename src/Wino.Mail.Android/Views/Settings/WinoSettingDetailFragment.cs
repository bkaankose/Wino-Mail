using Android.Content;
using Android.Views;
using AndroidX.RecyclerView.Widget;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Settings;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Settings;

/// <summary>A Settings page whose cards and expanders can open the generic detail page.</summary>
public interface ISettingsDetailOwner
{
    /// <summary>The cards and expanders the page shows, in order.</summary>
    IReadOnlyList<View> SettingSources();
}

/// <summary>
/// The one generic setting detail page (canvas Android SettingDetail and SettingsControls "WinoSettingDetailFragment"):
/// every value row, pushed card and expander opens it. The app bar title is the setting's header; the sections come from
/// <see cref="WinoSettingsDetailBuilder"/> in a RecyclerView with a ListAdapter (not PreferenceFragmentCompat, whose XML
/// preferences cannot bind the shared ViewModels). The setting is addressed by its path from the page under it, which
/// stays bound (hidden) while the detail shows, because the detail hosts that page's live controls: a choice written
/// here goes through the same binding the page's control uses, and the page's row shows the new value on return.
/// </summary>
public sealed class WinoSettingDetailFragment : WinoFragment, ISettingsPageTitleSource
{
    private const string OwnerKey = "settings.detail.owner";
    private const string PathKey = "settings.detail.path";

    private string? _title;

    protected override int LayoutResource => Resource.Layout.fragment_setting_detail;

    public string? PageTitle => _title;

    /// <summary>The card or expander shown, while the view exists.</summary>
    public View? Source { get; private set; }

    /// <summary>The setting's path from its page.</summary>
    public IReadOnlyList<string> Path => Entry?.State.TryGetValue(PathKey, out var path) == true && path is string[] segments ? segments : [];

    /// <summary>Records what a detail entry shows.</summary>
    internal static void Configure(PageEntry entry, PageEntry owner, string[] path)
    {
        entry.State[OwnerKey] = owner;
        entry.State[PathKey] = path;
    }

    /// <summary>The page a detail entry belongs to, or null for any other entry.</summary>
    internal static PageEntry? OwnerOf(PageEntry entry)
        => entry.State.TryGetValue(OwnerKey, out var owner) ? owner as PageEntry : null;

    protected override void OnBind(View view)
    {
        var context = view.RequireContext();
        var list = view.FindViewById<RecyclerView>(Resource.Id.setting_detail_list);
        view.SetBackgroundColor(WinoColors.SettingsListFill(context));
        Source = Resolve();
        Bindings.Own(new ActionDisposable(() => Source = null));
        if (list is null || Source is not { } source)
        {
            // The setting is gone (its list was rebuilt): go back to the page.
            view.Post(() => Activity?.OnBackPressedDispatcher.OnBackPressed());
            return;
        }
        _title = WinoSettingsNavigation.Title(source);
        list.SetLayoutManager(new LinearLayoutManager(context));
        var adapter = new WinoSettingsDetailAdapter();
        list.SetAdapter(adapter);
        adapter.Submit(WinoSettingsDetailBuilder.Build(context, source));
        (ParentFragment as SettingsHostFragment)?.UpdateChrome();
    }

    private View? Resolve()
    {
        if (Entry is not { } entry || OwnerOf(entry)?.Fragment is not ISettingsDetailOwner owner) return null;
        IReadOnlyList<View> sources = owner.SettingSources();
        View? current = null;
        foreach (var segment in Path)
        {
            current = WinoSettingsNavigation.Resolve(sources, segment);
            if (current is null) return null;
            sources = WinoSettingsNavigation.ChildSources(current);
        }
        return current;
    }
}

/// <summary>The detail page's list: at most 836 dp wide (the Settings column), centred by its parent.</summary>
[global::Android.Runtime.Register("com.winomail.mail.android.SettingsDetailList")]
public sealed class SettingsDetailList : RecyclerView
{
    public SettingsDetailList(Context context) : base(context) { }
    public SettingsDetailList(Context context, global::Android.Util.IAttributeSet? attrs) : base(context, attrs) { }
    private SettingsDetailList(IntPtr handle, global::Android.Runtime.JniHandleOwnership transfer) : base(handle, transfer) { }

    protected override void OnMeasure(int widthSpec, int heightSpec)
    {
        int max = WinoTokens.Px(Context ?? throw new InvalidOperationException("The list has no context."), Resource.Dimension.wino_settings_column_max);
        if (MeasureSpec.GetSize(widthSpec) > max)
            widthSpec = MeasureSpec.MakeMeasureSpec(max, MeasureSpecMode.Exactly);
        base.OnMeasure(widthSpec, heightSpec);
    }
}
