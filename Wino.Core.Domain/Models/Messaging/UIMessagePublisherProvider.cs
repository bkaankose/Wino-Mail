using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Messaging;

/// <summary>
/// Ambient access point for the active <see cref="IUIMessagePublisher"/>.
/// Defaults to publishing into the local <see cref="WeakReferenceMessenger"/> so the
/// single-process app and unit tests behave exactly as before. The background companion
/// process replaces <see cref="Current"/> with a pipe-forwarding publisher at startup.
/// Request records that are constructed outside of dependency injection use this
/// ambient instance for their optimistic UI change notifications.
/// </summary>
public static class UIMessagePublisherProvider
{
    private static IUIMessagePublisher _current = LocalUIMessagePublisher.Instance;

    public static IUIMessagePublisher Current
    {
        get => _current;
        set => _current = value ?? LocalUIMessagePublisher.Instance;
    }
}

/// <summary>
/// Default publisher that dispatches messages to the in-process weak reference messenger.
/// </summary>
public sealed class LocalUIMessagePublisher : IUIMessagePublisher
{
    public static LocalUIMessagePublisher Instance { get; } = new();

    public void Publish<TMessage>(TMessage message) where TMessage : class, IUIMessage
        => WeakReferenceMessenger.Default.Send(message);
}
