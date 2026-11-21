#if DEBUG
using System.Text;
using Wino.Mail.ViewModels;

namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>Hooks for <c>AccountSetupDebugActions</c>: the open page, its state without the address or any secret, and the widget setters.</summary>
internal sealed partial class AccountSetupView
{
    private static WeakReference<AccountSetupView>? _debugCurrent;

    public static AccountSetupView? DebugCurrent => _debugCurrent is not null && _debugCurrent.TryGetTarget(out var view) && !view.IsReleased ? view : null;

    public AccountSetupPageViewModel DebugViewModel => ViewModel;

    private IDisposable DebugTrack()
    {
        _debugCurrent = new WeakReference<AccountSetupView>(this);
        return new Presentation.Linux.ActionDisposable(() =>
        {
            if (_debugCurrent is not null && _debugCurrent.TryGetTarget(out var view) && ReferenceEquals(view, this)) _debugCurrent = null;
        });
    }

    public string DebugDescribe()
    {
        var vm = ViewModel;
        var text = new StringBuilder();
        text.AppendLine($"phase={vm.Phase} step={vm.Step} shown={_shownStep} route={vm.Route} signIn={vm.SignInKind} provider={vm.RouteProviderType}/{vm.RouteSpecialImapProvider}");
        text.AppendLine($"title='{AddressAwareTitle(vm)}' subtitle='{vm.StepSubtitle}' caption='{vm.StepCaption}'");
        text.AppendLine($"primary={(vm.IsPrimaryVisible ? vm.PrimaryText : "hidden")} primarySensitive={_primary.GetSensitive()} back={vm.IsBackVisible}/{vm.CanGoBack} footer={vm.IsFooterVisible} social={(vm.IsFooterSocialSignInVisible ? vm.NativeSignInButtonText : "hidden")}");
        text.AppendLine($"known={vm.IsKnownCardVisible} resolving={vm.IsResolvingVisible} notFound={vm.IsNotFoundVisible} notFoundTitle='{vm.NotFoundTitle}' changeProvider={vm.IsChangeProviderVisible} chooseProvider={vm.IsChooseProviderVisible}");
        text.AppendLine($"shelf={vm.ProviderShelfItems.Count} shelfVisible={vm.IsProviderShelfVisible} matched={vm.HasMatchedProvider}:{vm.MatchedProviderGlyph} emailField={vm.IsEmailFieldVisible} local={vm.IsLocalOnlyMode} color={vm.SelectedColor?.Hex ?? "none"}");
        text.AppendLine($"choices={vm.StepChoices.Count} selected={vm.SelectedChoice?.Label ?? "none"} appRows={vm.AppRows.Count} results={vm.ResultRows.Count} steps={vm.Steps.Count}");
        text.AppendLine($"apps={string.Join(" | ", vm.AppRows.Select(row => $"{row.Kind}:on={row.IsOn}:source={row.SelectedOption?.Label}:server={row.IsServerEntryShown}:note='{row.Note}':error='{row.Error}'"))}");
        text.AppendLine($"serverDetails={vm.IsServerDetailsVisible} incoming='{vm.ServerDetailsIncoming}' outgoing='{vm.ServerDetailsOutgoing}' caldav='{vm.ServerDetailsCalDav}' carddav='{vm.ServerDetailsCardDav}'");
        text.AppendLine($"password={vm.IsPasswordPanelVisible} browser={vm.IsBrowserPanelVisible}/{vm.IsBrowserSignInButtonVisible} exchange={vm.IsExchangePasswordVisible} status={(vm.IsConnectionStatusVisible ? vm.ConnectionStatusText : "hidden")}");
        text.AppendLine($"failure={(vm.IsFailureOpen ? $"{vm.FailureSeverity}:{vm.FailureTitle}:{vm.FailureMessage}" : "none")} retry={vm.IsRetryVisible} another={vm.IsUseAnotherOptionVisible}");
        text.AppendLine($"errors identity='{vm.IdentityError}' name='{vm.AccountNameError}' features='{vm.FeaturesError}' reviewCaption='{vm.ReviewCaption}'");
        text.AppendLine($"progress added={vm.IsAddedVisible} failed={vm.IsSetupFailedVisible} steps={string.Join(" | ", vm.Steps.Select(step => $"{step.Kind}:{step.Status}"))}");
        text.AppendLine($"providers={string.Join(",", vm.ProviderChoices.Select(choice => choice.Kind))} current={vm.CurrentProviderChoice?.Kind.ToString() ?? "none"}");
        return text.ToString();
    }

    /// <summary>Sets the text of a named entry widget, as typing would; values are never logged.</summary>
    public bool DebugType(string field, string text)
    {
        Gtk.Editable? target = field switch
        {
            "email" => _email,
            "name" => _localName,
            _ => DebugField(field),
        };
        if (target is null) return false;
        target.SetText(text);
        return true;
    }

    private Gtk.Editable? DebugField(string field) => _fields.GetValueOrDefault(field);

    public void DebugShowProviderPicker() => ShowProviderPicker();

    public Gtk.Widget? DebugFocus => (Root.GetRoot() as Gtk.Window)?.GetFocus();
}
#endif
