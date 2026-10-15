using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Onboarding;

/// <summary>
/// Welcome (Windows WelcomePageV2 content in the Material look): the hero over the wallpaper with the logo, title and
/// description, then in the Wino zone the Windows page's actions: restore from the Wino Account or a backup file, the
/// import progress and status, the get-started description and the get-started button.
/// </summary>
public sealed class WelcomePageFragment : WinoFragment<WelcomePageV2ViewModel>
{
    protected override int LayoutResource => Resource.Layout.fragment_welcome;

    protected override void OnBind(View view, WelcomePageV2ViewModel viewModel, IDispatcher dispatcher)
    {
        WinoInsets.Apply(view.FindViewById(Resource.Id.welcome_insets) ?? view);

        Text(view, Resource.Id.welcome_title, Translator.WelcomeWindow_Title);
        // WelcomeWindow_AppDescription names Windows; the same line without the platform.
        Text(view, Resource.Id.welcome_description, Translator.Android_Welcome_Tagline);
        Text(view, Resource.Id.welcome_get_started_description, Translator.WelcomeWindow_GetStartedDescription);
        Text(view, Resource.Id.welcome_import_progress_text, Translator.WelcomeWindow_ImportInProgress);
        if (view.FindViewById<ImageView>(Resource.Id.welcome_logo) is { } logo) logo.ContentDescription = Translator.WelcomeWindow_Title;

        Button(view, Resource.Id.welcome_import_account, Translator.WelcomeWindow_ImportFromWinoAccount, viewModel.ImportFromWinoAccountCommand, dispatcher);
        Button(view, Resource.Id.welcome_import_file, Translator.WelcomeWindow_ImportFromJsonFile, viewModel.ImportFromJsonCommand, dispatcher);
        Button(view, Resource.Id.welcome_add_account, Translator.WelcomeWindow_GetStartedButton, viewModel.GetStartedCommand, dispatcher);

        if (view.FindViewById(Resource.Id.welcome_import_progress) is { } progress)
        {
            Bind(viewModel, dispatcher, nameof(WelcomePageV2ViewModel.IsImportInProgress), vm => vm.IsImportInProgress,
                running => progress.Visibility = running ? ViewStates.Visible : ViewStates.Gone);
        }
        if (view.FindViewById<TextView>(Resource.Id.welcome_status) is { } status)
        {
            Bind(viewModel, dispatcher, nameof(WelcomePageV2ViewModel.ImportStatusMessage), vm => vm.ImportStatusMessage, message =>
            {
                status.Text = message;
                status.Visibility = string.IsNullOrWhiteSpace(message) ? ViewStates.Gone : ViewStates.Visible;
            });
        }
    }

    private void Button(View root, int id, string text, System.Windows.Input.ICommand command, IDispatcher dispatcher)
    {
        if (root.FindViewById<MaterialButton>(id) is not { } button) return;
        button.Text = text;
        BindCommand(button, command, dispatcher);
    }

    private static void Text(View root, int id, string text)
    {
        if (root.FindViewById<TextView>(id) is { } label) label.Text = text;
    }
}
