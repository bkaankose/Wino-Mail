using Microsoft.Extensions.DependencyInjection;
using UIKit;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Views.Mail;

namespace Wino.Mail.UIKit.Phone;

/// <summary>The heights a phone sheet may take (UISheetPresentationController detents).</summary>
public enum PhoneSheetSize
{
    /// <summary>Half height first, draggable to full (Move, the daily briefing).</summary>
    MediumAndLarge,

    /// <summary>Full height only (forms with a keyboard, such as the search filters).</summary>
    Large
}

/// <summary>
/// The iPhone presentation surface: the tab bar shell, rendering-frame and pushed pages on the selected tab's stack (the
/// reader over the message list, with the reader toolbar attached and the tab bar hidden while it is on top), and page
/// sheets, optionally with detents.
/// </summary>
public sealed class PhoneShellHost(IServiceProvider services) : UIKitShellHostBase
{
    public override UIKitShellViewController CreateShell() => services.GetRequiredService<PhoneShellViewController>();

    public override bool ShowInRenderingFrame(UIKitShellViewController shell, UIViewController controller) => Push(shell, controller);

    public override bool Push(UIKitShellViewController? shell, UIViewController controller)
    {
        // The shared reader: its own bottom toolbar replaces the tab bar while it is on top (Mail on iPhone).
        if (OpenSheet is null && shell is PhoneShellViewController phone && controller is IMailReaderCommands reader)
        {
            controller.HidesBottomBarWhenPushed = true;
            phone.AttachReaderToolbar(controller, reader);
        }
        return base.Push(shell, controller);
    }

    protected override UIModalPresentationStyle SheetStyle => UIModalPresentationStyle.PageSheet;

    /// <summary>Presents <paramref name="controller"/> as a page sheet with the grabber and the given detents.</summary>
    public UINavigationController PresentSheet(UIViewController controller, PhoneSheetSize size, Action dismissedByUser)
    {
        var sheet = PresentSheet(controller, UIKitPageHost.Sheet, dismissedByUser);
        if (sheet.SheetPresentationController is { } presentation)
        {
            presentation.Detents = size == PhoneSheetSize.Large
                ? [UISheetPresentationControllerDetent.CreateLargeDetent()]
                : [UISheetPresentationControllerDetent.CreateMediumDetent(), UISheetPresentationControllerDetent.CreateLargeDetent()];
            presentation.PrefersGrabberVisible = size == PhoneSheetSize.MediumAndLarge;
        }
        return sheet;
    }
}
