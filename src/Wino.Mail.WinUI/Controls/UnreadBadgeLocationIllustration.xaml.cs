using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels;

namespace Wino.Controls;

public enum UnreadBadgeIllustrationKind
{
    AccountBadge = 0,
    TaskbarBadge = 1,
    FolderBadges = 2
}

/// <summary>
/// A small drawing of one place an unread badge appears, drawn with the current count so the
/// setting next to it shows exactly what it changes.
/// </summary>
public sealed partial class UnreadBadgeLocationIllustration : UserControl
{
    [GeneratedDependencyProperty]
    public partial UnreadBadgeIllustrationKind Kind { get; set; }

    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsOn { get; set; }

    [GeneratedDependencyProperty]
    public partial int Count { get; set; }

    /// <summary>
    /// The folders drawn by <see cref="UnreadBadgeIllustrationKind.FolderBadges"/>, as a concrete
    /// list of <see cref="UnreadBadgeFolderViewModel"/> so WinRT can project it.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial object? Folders { get; set; }

    public UnreadBadgeLocationIllustration()
    {
        InitializeComponent();
    }

    public Visibility GetKindVisibility(UnreadBadgeIllustrationKind kind, int visibleKind)
        => (int)kind == visibleKind ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GetOffVisibility(bool isOn) => isOn ? Visibility.Collapsed : Visibility.Visible;

    public string FormatCount(int count) => count > 99 ? "99+" : count.ToString();
}
