#nullable enable

using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.ViewModels.Navigation;

/// <summary>Which mode the shell shows for an account event, shared by every head's shell policy.</summary>
public static class ShellModeSelection
{
    /// <summary>The first mode the new account provides, in the order Mail, Calendar, Tasks, Contacts.</summary>
    public static WinoApplicationMode ForCreatedAccount(MailAccount account)
        => account.IsMailAccessGranted ? WinoApplicationMode.Mail
            : account.IsCalendarAccessGranted ? WinoApplicationMode.Calendar
            : account.IsTaskAccessGranted ? WinoApplicationMode.Tasks
            : WinoApplicationMode.Contacts;
}
