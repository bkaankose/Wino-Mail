namespace Wino.Mail.Linux.Views.Settings;

/// <summary>A Settings page whose title follows its content (an account name). <see cref="PageTitleChanged"/> may arrive on any thread.</summary>
internal interface ISettingsPageTitleSource
{
    string? PageTitle { get; }
    string? PageDescription => null;
    event EventHandler? PageTitleChanged;
}

/// <summary>A Settings page that takes a new parameter while it is shown (the checkout return on Wino Account); true means handled in place.</summary>
internal interface ISettingsPageParameterReceiver
{
    Task<bool> ReceiveParameterAsync(object parameter);
}
