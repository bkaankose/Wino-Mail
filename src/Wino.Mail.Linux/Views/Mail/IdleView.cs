using Wino.Core.Domain;
using Wino.Core.Domain.Messaging.Client.Shell;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>The reading pane while no message is open.</summary>
internal sealed class IdleView : WinoView<IdlePageViewModel>
{
    public IdleView(IdlePageViewModel viewModel, WinoViewServices services) : base(viewModel, services)
    {
        var idle = Adw.StatusPage.New();
        idle.SetIconName("mail-read-symbolic");
        idle.SetTitle(Translator.NoMailSelected);
        Root = idle;
        Subscribe<LanguageChanged>(_ => idle.SetTitle(Translator.NoMailSelected));
    }

    public override Gtk.Widget Root { get; }
}
