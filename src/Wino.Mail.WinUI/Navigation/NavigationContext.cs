#nullable enable

using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.WinUI.Navigation;

/// <summary>
/// Everything a re-entry rule needs to decide whether a navigation request should actually
/// move the frame. Built by the navigation service just before it would navigate.
/// </summary>
public sealed class NavigationContext
{
    public required WinoPage Page { get; init; }

    public required NavigationRoute Route { get; init; }

    /// <summary>
    /// Frame the navigation is targeting.
    /// </summary>
    public required Frame Frame { get; init; }

    public required WinoApplicationMode Mode { get; init; }

    /// <summary>
    /// Parameter the caller asked to navigate with.
    /// </summary>
    public object? Parameter { get; init; }

    public object? CurrentContent => Frame.Content;

    /// <summary>
    /// The frame already shows the requested page type.
    /// </summary>
    public bool IsTargetActive => Frame.Content?.GetType() == Route.PageType;

    /// <summary>
    /// The requested page type is the entry we would return to by going back once.
    /// </summary>
    public bool IsTargetOnTopOfBackStack
        => Frame.CanGoBack &&
           Frame.BackStack.Count > 0 &&
           Frame.BackStack[^1].SourcePageType == Route.PageType;
}
