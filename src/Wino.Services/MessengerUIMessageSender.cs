using System;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public sealed class MessengerUIMessageSender : IUIMessageSender
{
    private readonly IMessenger _messenger;

    public MessengerUIMessageSender(IMessenger messenger)
    {
        _messenger = messenger ?? throw new ArgumentNullException(nameof(messenger));
    }

    public void Send<TMessage>(TMessage message) where TMessage : class
        => _messenger.Send(message);
}
