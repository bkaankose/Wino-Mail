using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Mail.Compose;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class LinuxPageRegistry
{
    partial void RegisterComposePages()
    {
        Register<ComposeView>(WinoPage.ComposePage, LinuxPageHost.RenderingFrame);
    }
}
