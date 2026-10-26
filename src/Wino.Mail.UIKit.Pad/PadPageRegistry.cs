using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Pad.Mail;
using Wino.Mail.UIKit.Views.Mail;
using Wino.Mail.UIKit.Views.Settings;

namespace Wino.Mail.UIKit.Pad;

/// <summary>
/// iPad routes. Onboarding comes from <see cref="UIKitPageRegistry"/>. A mode's root page is
/// <see cref="UIKitPageHost.ShellContent"/> (the list column), the reader, composer and idle pages are
/// <see cref="UIKitPageHost.RenderingFrame"/> (the reader column, <see cref="PadRenderingFrameHost"/>), drill-ins are
/// <see cref="UIKitPageHost.Push"/>, editors are <see cref="UIKitPageHost.Sheet"/> and Settings pages are a
/// <see cref="UIKitPageHost.FullScreenSheet"/> (<see cref="SettingsPageRoutes"/>). Other features add their routes in partial files.
/// </summary>
public sealed partial class PadPageRegistry : UIKitPageRegistry
{
    partial void RegisterCalendarPages();

    public PadPageRegistry()
    {
        RegisterCalendarPages();
        // Mail mode: the list column. Its selection routes MailRenderingPage, ComposePage and IdlePage to the reader column.
        Register<MailListPageViewController>(WinoPage.MailListPage, UIKitPageHost.ShellContent);
        this.RegisterMailPages(phone: false);
        this.RegisterSettingsPages();
        RegisterContactsPages();
        RegisterTasksPages();
    }
}
