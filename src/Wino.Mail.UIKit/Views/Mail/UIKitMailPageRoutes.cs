using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Infrastructure;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// The idiom-neutral Mail page routes (Mac MacPageRegistry.Mail). The phone and pad registries call
/// <see cref="RegisterMailPages"/> from their constructors:
/// <list type="bullet">
/// <item>iPad: the reader, compose and the idle page in the reader column (<see cref="UIKitPageHost.RenderingFrame"/>).</item>
/// <item>iPhone: the reader is pushed (<see cref="UIKitPageHost.RenderingFrame"/>, which the phone host pushes); compose is a
/// full-screen sheet; the idle page is not routed (an iPhone never shows an idle reader; the shell keeps the list).</item>
/// </list>
/// What's New and the Daily briefing are not WinoPage routes; their presenters show them (Views/Extras).
/// </summary>
public static class UIKitMailPageRoutes
{
    public static UIKitPageRegistry RegisterMailPages(this UIKitPageRegistry registry, bool phone)
    {
        registry.Register<MailRenderingPageViewController>(WinoPage.MailRenderingPage, UIKitPageHost.RenderingFrame);
        registry.Register<ComposePageViewController>(WinoPage.ComposePage, phone ? UIKitPageHost.FullScreenSheet : UIKitPageHost.RenderingFrame);
        if (!phone) registry.Register<IdlePageViewController>(WinoPage.IdlePage, UIKitPageHost.RenderingFrame);
        return registry;
    }
}
