using System;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.ViewModels;
using MailCal.Mail.ViewModels;

namespace MailCal.Shell.ViewModels;

/// <summary>Lazy composition factories supplied by the application host.</summary>
public sealed class ShellMenuProviderResolver(
    Func<IMailShellClient> mail,
    Func<ICalendarShellClient> calendar,
    Func<ContactsPageViewModel> contacts,
    Func<ToDoPageViewModel> tasks,
    Func<SettingsMenuProvider> settings) : IShellMenuProviderResolver
{
    public IShellMenuProvider Resolve(MailCalApplicationMode mode)
        => mode switch
        {
            MailCalApplicationMode.Mail => mail(),
            MailCalApplicationMode.Calendar => calendar(),
            MailCalApplicationMode.Contacts => contacts(),
            MailCalApplicationMode.Tasks => tasks(),
            MailCalApplicationMode.Settings => settings(),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
}
