using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Shell;
using Wino.Presentation.Controls;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Shell;

/// <summary>The content of a mode whose root page has no route on Linux yet.</summary>
internal static class ModePlaceholderView
{
    public static Gtk.Widget Create(WinoApplicationMode mode)
    {
        var page = Adw.StatusPage.New();
        page.SetTitle(ShellModeActivator.ModeTitle(mode));
        page.SetDescription(Translator.ComingSoon);
        page.AddCssClass("compact");
        var segment = WinoModeSegments.All.FirstOrDefault(segment => segment.Mode == mode);
        if (segment is not null) page.SetChild(new WinoIcon(segment.Regular, 48, WinoIconMode.FollowStyle).Root);
        return page;
    }
}
