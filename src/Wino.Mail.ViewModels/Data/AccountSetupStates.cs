#nullable enable
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
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

/// <summary>
/// The step the setup page shows. It derives from <see cref="AccountSetupPhase"/>. The IMAP route signs in before
/// <see cref="Review"/>, so the review can say what the signed-in server offers; every other route reviews first,
/// because the chosen apps decide what the sign-in asks for.
/// </summary>
public enum AccountSetupStep
{
    Address,

    /// <summary>After Advanced setup on the address step: an IMAP or POP3 server, or an Exchange Server.</summary>
    ServerKind,
    Discovery,
    SignInMethod,
    SignIn,
    Review,
    Progress
}

public enum AccountSetupAppKind
{
    Mail,
    Calendar,
    Contacts,
    Tasks
}

/// <summary>What the signed-in IMAP account's server answered for a DAV service.</summary>
public enum AccountSetupDavAvailability
{
    NotFound,
    Available,
    NeedsSignIn,
    Failed
}

/// <summary>One entry of a review row's drop-down: where the app's data lives, or for Mail how far back it downloads.</summary>
public sealed class AccountSetupSourceOption
{
    public AccountSetupSourceOption(string id, string label)
    {
        Id = id;
        Label = label;
    }

    public string Id { get; }
    public string Label { get; }

    public override string ToString() => Label;
}

/// <summary>
/// One app on the review step: a switch for using it with this account and a drop-down for where its data lives.
/// The page view model owns the rules; the row only carries the state the page draws.
/// </summary>
public sealed partial class AccountSetupAppRow : ObservableObject
{
    public AccountSetupAppRow(AccountSetupAppKind kind, string title, WinoIconGlyph glyph, bool isSwitchVisible, List<AccountSetupSourceOption> options)
    {
        Kind = kind;
        Title = title;
        Glyph = glyph;
        IsSwitchVisible = isSwitchVisible;
        Options = options;
    }

    public AccountSetupAppKind Kind { get; }
    public string Title { get; }
    public WinoIconGlyph Glyph { get; }
    public bool IsSwitchVisible { get; }
    public List<AccountSetupSourceOption> Options { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSourceVisible))]
    [NotifyPropertyChangedFor(nameof(IsServerEntryShown))]
    [NotifyPropertyChangedFor(nameof(IsNoteShown))]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial bool IsOn { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial AccountSetupSourceOption? SelectedOption { get; set; }

    /// <summary>
    /// Under the title when the account cannot simply sync the app: "Not found on the server", "Needs its own
    /// sign-in". Empty when it can.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNoteShown))]
    public partial string Note { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerEntryShown))]
    public partial bool IsServerEntryVisible { get; set; }

    [ObservableProperty]
    public partial string ServerUrl { get; set; } = string.Empty;

    public bool HasStatus => !string.IsNullOrEmpty(StatusText);
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool IsSourceVisible => IsOn && Options.Count > 1;
    public bool IsServerEntryShown => IsOn && IsServerEntryVisible;
    public bool IsNoteShown => IsOn && !string.IsNullOrEmpty(Note);

    public string AutomationName => !IsOn
        ? $"{Title}, {Translator.AccountSetup_Choice_Off}"
        : SelectedOption == null ? Title : $"{Title}, {SelectedOption.Label}";
}

/// <summary>One line of the result under "Account added": the app and where its data lives.</summary>
public sealed record AccountSetupResultRow(WinoIconGlyph Glyph, string Title, string Detail)
{
    public string AutomationName => $"{Title}: {Detail}";
}

/// <summary>One item of a step's single-selection list.</summary>
public sealed class AccountSetupChoice
{
    public AccountSetupChoice(string id, string label, bool isRecommended = false, string? caption = null, string? description = null, WinoIconGlyph glyph = WinoIconGlyph.None)
    {
        Id = id;
        Label = label;
        IsRecommended = isRecommended;
        CaptionText = caption ?? (isRecommended ? Translator.AccountSetup_Recommended : string.Empty);
        Description = description ?? string.Empty;
        Glyph = glyph;
    }

    /// <summary>The icon before the label, such as the server kind's brand mark. None for a choice without one.</summary>
    public WinoIconGlyph Glyph { get; }

    public bool HasGlyph => Glyph != WinoIconGlyph.None;

    public string Id { get; }
    public string Label { get; }
    public bool IsRecommended { get; }
    public string CaptionText { get; }

    /// <summary>A sentence under the label that says what choosing it leads to. Empty for a choice that needs none.</summary>
    public string Description { get; }

    public bool HasCaption => !string.IsNullOrEmpty(CaptionText);

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    public string Caption => HasCaption ? $"({CaptionText})" : string.Empty;

    public string AutomationName => (HasCaption ? $"{Label}, {CaptionText}" : Label) + (HasDescription ? $", {Description}" : string.Empty);

    public override string ToString() => AutomationName;
}

public enum AccountSetupKnownItemKind
{
    Identity,
    Provider,
    Feature,
    SignIn,

    /// <summary>Why the provider was chosen, for domain-level evidence or a manual choice; the strip's tertiary line.</summary>
    Reason
}

public enum AccountSetupProviderChoiceKind
{
    Google,
    Microsoft,
    Exchange,
    ICloud,
    Yahoo,
    Imap,
    Pop3
}

/// <summary>One mark on the address step's "Works with" shelf; the typed domain lights its provider and dims the rest.</summary>
public sealed record AccountSetupProviderShelfItem(string Title, WinoIconGlyph Glyph, string Label, bool IsMatched, bool IsDimmed)
{
    public bool HasLabel => !string.IsNullOrEmpty(Label);
    public bool HasGlyph => !HasLabel;
}

/// <summary>One entry of the "Change provider" picker and the route it forces.</summary>
public sealed class AccountSetupProviderChoice
{
    public AccountSetupProviderChoice(
        AccountSetupProviderChoiceKind kind,
        string title,
        string description,
        WinoIconGlyph glyph,
        AccountRoute route,
        SpecialImapProvider specialImapProvider = SpecialImapProvider.None,
        bool isPop3 = false)
    {
        IsPop3 = isPop3;
        Kind = kind;
        Title = title;
        Description = description;
        Glyph = glyph;
        Route = route;
        SpecialImapProvider = specialImapProvider;
    }

    public AccountSetupProviderChoiceKind Kind { get; }
    public string Title { get; }
    public string Description { get; }
    public WinoIconGlyph Glyph { get; }
    public AccountRoute Route { get; }
    public SpecialImapProvider SpecialImapProvider { get; }

    /// <summary>The choice runs POP3 discovery on the IMAP route.</summary>
    public bool IsPop3 { get; }

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    public string AutomationName => HasDescription ? $"{Title}, {Description}" : Title;

    public override string ToString() => AutomationName;
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
    ExchangePasswordCheck
}
