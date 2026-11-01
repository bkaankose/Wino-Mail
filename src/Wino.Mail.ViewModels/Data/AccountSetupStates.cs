#nullable enable
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Mail.ViewModels.Data;

/// <summary>Where the address-first setup is. <see cref="AccountSetupStep"/> derives from it.</summary>
public enum AccountSetupPhase
{
    Identity,
    Resolving,
    NotFound,
    Features,
    SignIn,
    Checking,
    Adding,
    Added,
    Failed
}

public enum AccountSetupSignInKind
{
    None,
    Microsoft,
    Google,
    Imap,
    Exchange
}

public enum AccountSetupOAuthState
{
    Idle,
    Waiting,
    SignedIn
}

/// <summary>The status line under the sign-in controls, aggregated over the checks and the sign-in.</summary>
public enum AccountSetupConnectionStatus
{
    None,
    Waiting,
    Checking,
    NeedsInput,
    Connected,
    Failed
}

public enum AccountSetupCheckKind
{
    Incoming,
    Outgoing,
    Calendar,
    Contacts,
    ProviderAccount
}

/// <summary>The step the setup page shows. It derives from <see cref="AccountSetupPhase"/> and the feature and sign-in method substeps.</summary>
public enum AccountSetupStep
{
    Address,
    Discovery,
    Mail,
    Calendar,
    Contacts,
    Tasks,
    SignInMethod,
    SignIn,
    Progress
}

/// <summary>One item of a step's single-selection list.</summary>
public sealed class AccountSetupChoice
{
    public AccountSetupChoice(string id, string label, bool isRecommended = false, string? caption = null)
    {
        Id = id;
        Label = label;
        IsRecommended = isRecommended;
        CaptionText = caption ?? (isRecommended ? Translator.AccountSetup_Recommended : string.Empty);
    }

    public string Id { get; }
    public string Label { get; }
    public bool IsRecommended { get; }
    public string CaptionText { get; }

    public bool HasCaption => !string.IsNullOrEmpty(CaptionText);

    public string Caption => HasCaption ? $"({CaptionText})" : string.Empty;

    public string AutomationName => HasCaption ? $"{Label}, {CaptionText}" : Label;

    public override string ToString() => AutomationName;
}

public enum AccountSetupKnownItemKind
{
    Identity,
    Provider,
    Feature,
    SignIn
}

/// <summary>One fact on the known-so-far card above the step.</summary>
public sealed class AccountSetupKnownItem
{
    public AccountSetupKnownItem(AccountSetupKnownItemKind kind, string text, string helpText = "")
    {
        Kind = kind;
        Text = text;
        HelpText = helpText;
    }

    public AccountSetupKnownItemKind Kind { get; }
    public string Text { get; }
    public string HelpText { get; }
}

/// <summary>One connection row under the sign-in block: incoming, outgoing, calendar, contacts, or the provider account.</summary>
public sealed partial class AccountSetupConnectionCheck : ObservableObject
{
    public AccountSetupConnectionCheck(AccountSetupCheckKind kind, string title)
    {
        Kind = kind;
        Title = title;
    }

    public AccountSetupCheckKind Kind { get; }
    public string Title { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsTesting))]
    [NotifyPropertyChangedFor(nameof(IsSucceeded))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial ConnectionTestState State { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial bool NeedsPassword { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial string Message { get; set; } = string.Empty;

    public bool IsTesting => State == ConnectionTestState.Testing;
    public bool IsSucceeded => State == ConnectionTestState.Succeeded;
    public bool IsFailed => State == ConnectionTestState.Failed;
    public bool IsWaiting => State == ConnectionTestState.NotTested;
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    public string StatusText => NeedsPassword && State == ConnectionTestState.NotTested
        ? Translator.AccountSetup_Status_NeedsPassword
        : State switch
        {
            ConnectionTestState.Testing => Translator.ImapSetup_StatusTesting,
            ConnectionTestState.Succeeded => Translator.ImapSetup_StatusConnected,
            ConnectionTestState.Failed => Translator.ImapSetup_StatusFailed,
            _ => Translator.ImapSetup_StatusNotTested
        };

    public string AutomationName => HasMessage
        ? $"{Title}: {StatusText}. {Message}"
        : $"{Title}: {StatusText}";
}

/// <summary>One IMAP sign-in choice as the option group shows it.</summary>
public sealed class AccountSetupImapOption
{
    public AccountSetupImapOption(ImapSignInOption option, string title, string description)
    {
        Option = option;
        Title = title;
        Description = description;
    }

    public ImapSignInOption Option { get; }
    public string Title { get; }
    public string Description { get; }

    public ImapSignInOptionKind Kind => Option.Kind;
    public bool IsBrowser => Option.Kind == ImapSignInOptionKind.Browser;
    public bool IsPassword => !IsBrowser;

    public override string ToString() => Title;
}

/// <summary>What the failure InfoBar's Try again repeats.</summary>
public enum AccountSetupRetryAction
{
    None,
    NativeSignIn,
    BrowserSignIn,
    PasswordCheck,
    DavCheck,
    ExchangePasswordCheck
}
