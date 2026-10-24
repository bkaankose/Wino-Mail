using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Android.Views.Settings.Personalization;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Settings;

public static partial class SettingsPageRoutes
{
    static partial void RegisterPersonalizationPages(AndroidPageRegistry registry)
    {
        const AndroidPageHost host = AndroidPageHost.Settings;
        // The page drives the theme service itself (iOS and Mac): no Windows navigation work on the ViewModel.
        // The app mode options are the one part of the shared page the fragment shows as the ViewModel builds it.
        registry.Register<PersonalizationPageFragment, PersonalizationPageViewModel>(WinoPage.PersonalizationPage, host,
            static (vm, _, _) => vm.ActivateAppModeOptionsAsync(),
            static vm => { vm.DeactivateAppModeOptions(); return Task.CompletedTask; });
        registry.Register<ApplicationThemeGalleryPageFragment, ApplicationThemeGalleryPageViewModel>(WinoPage.ApplicationThemeGalleryPage, host,
            static (vm, mode, parameter) => vm.InitializeNavigationAsync(mode, parameter));
        registry.Register<ApplicationThemeEditorPageFragment, ApplicationThemeEditorPageViewModel>(WinoPage.ApplicationThemeEditorPage, host,
            static async (vm, mode, parameter) =>
            {
                await vm.InitializeNavigationAsync(mode, parameter);
                // The captured element theme can be Default: tell the ViewModel what the app renders right now.
                vm.ResolveSystemPaletteMode(WinoColors.IsDark(global::Android.App.Application.Context));
            },
            static async vm =>
            {
                // Restores the theme in use unless the theme was saved or deleted.
                try { await vm.ReleasePreviewAsync(); }
                catch (Exception exception) { Serilog.Log.Warning(exception, "Releasing the theme preview failed."); }
                vm.OnNavigatedFrom(NavigationMode.New, null);
            });
    }
}
