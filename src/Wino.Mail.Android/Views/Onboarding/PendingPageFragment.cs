using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Android.Infrastructure;

namespace Wino.Mail.Android.Views.Onboarding;

/// <summary>
/// Stands in for pages that later items build: provider selection and account setup (T5), compose (T8) and Settings
/// (T9). It names the page and offers Back, so the navigation and back handling around it can be exercised now.
/// </summary>
public sealed class PendingPageFragment : WinoFragment
{
    protected override int LayoutResource => Resource.Layout.fragment_provider_placeholder;

    protected override void OnBind(View view)
    {
        SystemBarInsets.Apply(view.FindViewById(Resource.Id.placeholder_insets) ?? view);
        var page = Entry?.Page ?? WinoPage.None;
        var (title, subtitle) = page switch
        {
            WinoPage.ProviderSelectionPage => (Translator.ProviderSelection_Title, Translator.ProviderSelection_Subtitle),
            WinoPage.ComposePage => (Translator.MenuNewMail, string.Empty),
            WinoPage.SettingsPage => (Translator.MenuSettings, string.Empty),
            _ => ("Wino Mail", string.Empty)
        };
        if (view.FindViewById<TextView>(Resource.Id.placeholder_title) is { } titleLabel) titleLabel.Text = title;
        if (view.FindViewById<TextView>(Resource.Id.placeholder_subtitle) is { } subtitleLabel) subtitleLabel.Text = subtitle;
        if (view.FindViewById<TextView>(Resource.Id.placeholder_message) is { } message) message.Text = Translator.Info_UnsupportedFunctionalityDescription;
        if (view.FindViewById<MaterialButton>(Resource.Id.placeholder_back) is { } back)
        {
            var navigation = MainApplication.Services?.GetService<AndroidNavigationService>();
            back.Text = Translator.Buttons_Back;
            // The shell placeholder is a root; onboarding pages always have Welcome behind them.
            back.Visibility = page == WinoPage.None ? ViewStates.Gone : ViewStates.Visible;
            OnClick(back, () => navigation?.TryHandleBack());
        }
    }
}
