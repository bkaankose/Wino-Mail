using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MailCal.Mail.ViewModels;

namespace MailCal.Controls;

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
    /// The folders drawn by <see cref="UnreadBadgeIllustrationKind.FolderBadges"/>, typed as a concrete
    /// list so Native AOT can project it.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial System.Collections.Generic.List<UnreadBadgeFolderViewModel>? Folders { get; set; }

    public UnreadBadgeLocationIllustration()
    {
        InitializeComponent();
    }

    public Visibility GetKindVisibility(UnreadBadgeIllustrationKind kind, int visibleKind)
        => (int)kind == visibleKind ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GetOffVisibility(bool isOn) => isOn ? Visibility.Collapsed : Visibility.Visible;

    public string FormatCount(int count) => count > 99 ? "99+" : count.ToString();
}
