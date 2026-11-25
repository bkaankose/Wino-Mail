using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Mail;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class LinuxPageRegistry
{
    partial void RegisterMailPages()
    {
        Register<MailListView>(WinoPage.MailListPage, LinuxPageHost.ShellContent);
        Register<MailReaderView>(WinoPage.MailRenderingPage, LinuxPageHost.RenderingFrame);
        Register<IdleView>(WinoPage.IdlePage, LinuxPageHost.RenderingFrame);
    }
}
