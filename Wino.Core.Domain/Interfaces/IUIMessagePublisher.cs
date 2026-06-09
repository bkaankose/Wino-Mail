namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Abstraction over publishing <see cref="IUIMessage"/> notifications to the UI layer.
/// In single-process mode messages are dispatched to the local messenger.
/// When synchronization runs in the background companion process, the publisher
/// additionally forwards each message over the IPC channel to connected clients.
/// </summary>
public interface IUIMessagePublisher
{
    /// <summary>
    /// Publishes the given UI message to all listeners.
    /// </summary>
    void Publish<TMessage>(TMessage message) where TMessage : class, IUIMessage;
}
