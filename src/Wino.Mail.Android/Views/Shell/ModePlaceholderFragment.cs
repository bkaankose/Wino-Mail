using Android.OS;
using Android.Views;
using Android.Widget;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Android.Infrastructure;
using Wino.Presentation.Android;
using Wino.Presentation.Controls;

namespace Wino.Mail.Android.Views.Shell;

/// <summary>
/// A mode's content before its pages are built (Calendar, Contacts and To Do until T10), and the empty list pane while a
/// mode has no content page: the mode glyph and title in a Wino zone (the iOS ModePlaceholderViewController).
/// </summary>
public sealed class ModePlaceholderFragment : WinoFragment
{
    protected override int LayoutResource => Resource.Layout.fragment_mode_placeholder;

    // Shown with or without a routed entry (the shell's empty pane has none).
    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        if (Entry is null) Fill(view);
    }

    protected override void OnBind(View view) => Fill(view);

    private static void Fill(View view)
    {
        var shell = MainApplication.Services?.GetService<AndroidNavigationService>()?.Shell;
        var mode = shell?.ActiveMode ?? WinoApplicationMode.Mail;
        var segment = WinoModeSegments.AppModes.FirstOrDefault(candidate => candidate.Mode == mode);
        if (view.FindViewById<WinoIconView>(Resource.Id.mode_placeholder_glyph) is { } glyph)
        {
            glyph.GlyphSize = 48;
            glyph.Icon = segment?.Regular ?? WinoIconGlyph.Mail;
        }
        if (view.FindViewById<TextView>(Resource.Id.mode_placeholder_title) is { } title) title.Text = segment?.Title ?? string.Empty;
        if (view.FindViewById<TextView>(Resource.Id.mode_placeholder_message) is { } message)
        {
            message.Text = mode == WinoApplicationMode.Mail ? string.Empty : Translator.Info_UnsupportedFunctionalityDescription;
            message.Visibility = mode == WinoApplicationMode.Mail ? ViewStates.Gone : ViewStates.Visible;
        }
        if (view.FindViewById<ImageButton>(Resource.Id.mode_placeholder_navigation) is { } navigation)
        {
            navigation.SetImageDrawable(new AndroidX.AppCompat.Graphics.Drawable.DrawerArrowDrawable(view.RequireContext()) { Color = WinoColors.PrimaryText(view.RequireContext()) });
            navigation.ContentDescription = Translator.Android_Shell_OpenNavigation;
            navigation.Click += (_, _) => (FindChrome(view))?.ToggleNavigation();
        }
    }

    private static IShellChrome? FindChrome(View view)
        => (AndroidX.Fragment.App.FragmentManager.FindFragment(view) as AndroidX.Fragment.App.Fragment)?.ParentFragment as IShellChrome;
}
