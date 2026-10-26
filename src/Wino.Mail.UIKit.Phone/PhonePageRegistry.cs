using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Phone.Mail;
using Wino.Mail.UIKit.Views.Mail;
using Wino.Mail.UIKit.Views.Settings;

namespace Wino.Mail.UIKit.Phone;

/// <summary>
/// iPhone routes. Onboarding comes from <see cref="UIKitPageRegistry"/>. A mode's root page is
/// <see cref="UIKitPageHost.ShellContent"/> (in Mail the shell pushes it over Mailboxes), the reader and drill-ins are
/// <see cref="UIKitPageHost.Push"/>, compose and Settings pages are <see cref="UIKitPageHost.FullScreenSheet"/>, other
/// editors <see cref="UIKitPageHost.Sheet"/>. Other features add their routes in partial files.
/// </summary>
public sealed partial class PhonePageRegistry : UIKitPageRegistry
{
    public PhonePageRegistry()
    {
        RegisterMailPages();
        this.RegisterSettingsPages();
        RegisterContactsPages();
        RegisterTasksPages();
        RegisterCalendarPages();
    }

    partial void RegisterCalendarPages();

    /// <summary>
    /// The Mail flow: Mailboxes (the Mail stack root) → folder list → message. The list is the Mail mode's shell content,
    /// which the phone shell pushes over Mailboxes (a plain push route would bypass the shell's content lifecycle).
    /// The reader and the composer are the shared UIKit pages (<see cref="UIKitMailPageRoutes"/>); on iPhone the reader
    /// is pushed over the list (the phone host attaches its bottom toolbar) and a draft opens in a full-screen sheet.
    /// The idle page is not routed on iPhone. Move and the daily briefing are presented by the list and the shell.
    /// </summary>
    private void RegisterMailPages()
    {
        Register<MailListPageViewController>(WinoPage.MailListPage, UIKitPageHost.ShellContent);
        this.RegisterMailPages(phone: true);
        RegisterHost(WinoPage.MailRenderingPage, UIKitPageHost.Push);
    }
}
