#nullable enable

namespace Wino.Core.Domain.Models.Navigation;

/// <summary>
/// Outcome a detail page hands back to the page it returns to when going back.
/// </summary>
public enum NavigationResultKind
{
    Cancelled,
    Saved,
    Deleted
}

/// <summary>
/// Payload delivered to <see cref="Interfaces.IBackNavigationAware"/> destinations after
/// a back navigation completes. Detail view models publish it right before going back.
/// </summary>
public sealed class NavigationResult
{
    public NavigationResultKind Kind { get; }

    /// <summary>
    /// Entity the detail page acted on, when the destination needs to refresh a single row.
    /// </summary>
    public object? Payload { get; }

    private NavigationResult(NavigationResultKind kind, object? payload)
    {
        Kind = kind;
        Payload = payload;
    }

    public static NavigationResult Cancelled() => new(NavigationResultKind.Cancelled, null);

    public static NavigationResult Saved(object? payload = null) => new(NavigationResultKind.Saved, payload);

    public static NavigationResult Deleted(object? payload = null) => new(NavigationResultKind.Deleted, payload);
}
