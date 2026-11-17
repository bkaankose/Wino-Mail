namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Sends a UI message to the recipients of the messenger the caller was composed with.
/// Requests receive it from the pipeline, because they are created with <c>new</c> and cannot be injected.
/// </summary>
public interface IUIMessageSender
{
    void Send<TMessage>(TMessage message) where TMessage : class;
}
