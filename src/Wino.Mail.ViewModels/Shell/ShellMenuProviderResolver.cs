using System;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.ViewModels;
using Wino.Mail.ViewModels;

namespace Wino.Shell.ViewModels;

/// <summary>Lazy composition factories supplied by the application host.</summary>
public sealed class ShellMenuProviderResolver(
    Func<IMailShellClient> mail,
    Func<ICalendarShellClient> calendar,
    Func<ContactsPageViewModel> contacts,
    Func<ToDoPageViewModel> tasks,
    Func<SettingsMenuProvider> settings) : IShellMenuProviderResolver
{
    public IShellMenuProvider Resolve(WinoApplicationMode mode)
        => mode switch
        {
            WinoApplicationMode.Mail => mail(),
            WinoApplicationMode.Calendar => calendar(),
            WinoApplicationMode.Contacts => contacts(),
            WinoApplicationMode.Tasks => tasks(),
            WinoApplicationMode.Settings => settings(),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
}
