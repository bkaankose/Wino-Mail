using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Views.Mail;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Mail mode routes. Owned by the Mail feature.</summary>
public sealed partial class MacPageRegistry
{
    partial void RegisterMailPages()
    {
        Register<MailListPageViewController>(WinoPage.MailListPage, MacPageHost.ShellContent);
        Register<MailRenderingPageViewController>(WinoPage.MailRenderingPage, MacPageHost.RenderingFrame);
        Register<ComposePageViewController>(WinoPage.ComposePage, MacPageHost.RenderingFrame);
        Register<IdlePageViewController>(WinoPage.IdlePage, MacPageHost.RenderingFrame);
    }
}
