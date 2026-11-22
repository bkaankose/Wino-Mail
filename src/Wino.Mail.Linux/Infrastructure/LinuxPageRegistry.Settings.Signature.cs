using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Settings;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class LinuxPageRegistry
{
    partial void RegisterSettingsSignaturePages() => Register<SignatureManagementPage>(WinoPage.SignatureManagementPage, LinuxPageHost.Settings);
}
