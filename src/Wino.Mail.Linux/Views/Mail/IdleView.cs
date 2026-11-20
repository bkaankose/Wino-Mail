using Wino.Core.Domain;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>The reading pane while no message is open. Its header bar keeps the pane's window controls.</summary>
internal sealed class IdleView : WinoView<IdlePageViewModel>
{
    public IdleView(IdlePageViewModel viewModel, WinoViewServices services) : base(viewModel, services)
    {
        var idle = Adw.StatusPage.New();
        idle.SetIconName("mail-read-symbolic");
        idle.SetTitle(Translator.NoMailSelected);

        var view = Adw.ToolbarView.New();
        view.AddTopBar(Adw.HeaderBar.New());
        view.SetContent(idle);
        Root = view;
    }

    public override Gtk.Widget Root { get; }
}
