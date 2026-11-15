namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// The steps of the IMAP server page while an account is being added. Choosing capabilities
/// happens on the provider page before this one; editing an account shows no steps.
/// </summary>
public enum ImapSetupStep
{
    SignIn = 0,
    Servers = 1
}

/// <summary>
/// The sign-in step asks for the address first, so discovery can tell whether the server offers
/// secure sign-in before a password is requested.
/// </summary>
public enum ImapSignInStage
{
    Identity = 0,
    Method = 1
}

public enum ServerDiscoveryState
{
    NotRun = 0,
    Found = 1,
    NotFound = 2
}

public enum ConnectionTestState
{
    NotTested = 0,
    Testing = 1,
    Succeeded = 2,
    Failed = 3
}
