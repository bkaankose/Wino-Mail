using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Settings;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class LinuxPageRegistry
{
    partial void RegisterSettingsShortcutsPages() => Register<KeyboardShortcutsPage>(WinoPage.KeyboardShortcutsPage, LinuxPageHost.Settings);
}
