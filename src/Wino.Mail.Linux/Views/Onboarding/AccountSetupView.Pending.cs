using Wino.Core.Domain;

namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>Steps that are not built yet show the way to the manual server form.</summary>
internal sealed partial class AccountSetupView
{
    private Gtk.Widget BuildSignInStep() => Pending();
    private Gtk.Widget BuildProgressStep() => Pending();
    private Gtk.Widget? SignInFocusTarget() => null;

    private Gtk.Widget Pending()
    {
        var page = Adw.StatusPage.New();
        page.SetTitle(Translator.AccountSetup_SetUpManually);
        page.SetChild(_form.Button(Translator.AccountSetup_SetUpManually, ViewModel.OpenAdvancedSetupCommand, suggested: true));
        return page;
    }
}
