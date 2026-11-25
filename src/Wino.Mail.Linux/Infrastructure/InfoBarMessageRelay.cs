using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Client.Shell;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>Shows the <see cref="InfoBarMessageRequested"/> messages of the shared services through the dialog service.</summary>
internal sealed class InfoBarMessageRelay
{
    private readonly IMailDialogService _dialogs;

    private InfoBarMessageRelay(IMailDialogService dialogs) => _dialogs = dialogs;

    // The messenger holds recipients weakly: the caller keeps the relay for the life of the app.
    public static InfoBarMessageRelay Start(IMessenger messenger, IMailDialogService dialogs)
    {
        var relay = new InfoBarMessageRelay(dialogs);
        messenger.Register<InfoBarMessageRelay, InfoBarMessageRequested>(relay, static (recipient, message) => recipient.Show(message));
        return relay;
    }

    private void Show(InfoBarMessageRequested message)
    {
        if (!string.IsNullOrEmpty(message.ActionButtonTitle) && message.Action is { } action)
            _dialogs.InfoBarMessage(message.Title ?? string.Empty, message.Message ?? string.Empty, message.Severity, message.ActionButtonTitle, action);
        else
            _dialogs.InfoBarMessage(message.Title ?? string.Empty, message.Message ?? string.Empty, message.Severity);
    }
}
