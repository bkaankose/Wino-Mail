using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Pad.Shell;

/// <summary>
/// One suggestion row: an optional image or colour dot, a title, an optional subtitle and the caller's tag (returned by
/// <see cref="ShellSearchSuggestionController.Chosen"/>).
/// </summary>
public sealed record ShellSearchSuggestion(string Title, string? Subtitle, object? Tag, UIImage? Image = null, UIColor? Dot = null);

/// <summary>
/// A suggestion list under the toolbar search field (the Mac ShellSearchSuggestionList, the Windows AutoSuggestBox popup).
/// It floats in the anchor's window as a popover-styled card that never takes the keyboard focus, so the field keeps
/// editing while the user types. The owner forwards ↑/↓ (<see cref="Move"/>), Return (<see cref="TryChoose"/>) and Esc
/// (<see cref="Close"/>) from <c>IShellSearchSuggestionTarget.SearchFieldCommand</c>; a tap on a row chooses it.
/// Pages that implement <c>IShellSearchSuggestionTarget</c> (Calendar, To Do) own one. Main thread only.
/// </summary>
public sealed class ShellSearchSuggestionController : IDisposable
{
    private const double RowHeight = 48;
    private const double CompactRowHeight = 36;
    private const double MaximumHeight = 360;

    private readonly WinoSurfaceView _card = new() { Fill = UIColor.SystemBackground, CornerRadius = WinoStyle.PopoverRadius, Stroke = WinoStyle.ZoneStroke, StrokeWidth = 1 };
    private readonly UITableView _table = new(CGRect.Empty, UITableViewStyle.Plain) { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly SuggestionSource _source;
    private IReadOnlyList<ShellSearchSuggestion> _suggestions = Array.Empty<ShellSearchSuggestion>();
    private int _highlighted = -1;
    private bool _disposed;

    public ShellSearchSuggestionController()
    {
        _card.TranslatesAutoresizingMaskIntoConstraints = true;
        _card.Layer.ShadowColor = UIColor.Black.CGColor;
        _card.Layer.ShadowOpacity = 0.18f;
        _card.Layer.ShadowRadius = 14;
        _card.Layer.ShadowOffset = new CGSize(0, 4);
        _table.BackgroundColor = UIColor.Clear;
        _table.SeparatorStyle = UITableViewCellSeparatorStyle.None;
        _table.Layer.CornerRadius = (nfloat)WinoStyle.PopoverRadius;
        _table.ClipsToBounds = true;
        _source = new SuggestionSource(this);
        _table.Source = _source;
        WinoLayout.Fill(_table, _card);
    }

    /// <summary>Raised with the chosen row (tap, or Return through <see cref="TryChoose"/>). The list is closed first.</summary>
    public event EventHandler<ShellSearchSuggestion>? Chosen;

    /// <summary>Raised when the list closes for any reason.</summary>
    public event EventHandler? Closed;

    /// <summary>Minimum width; the list is at least as wide as its anchor.</summary>
    public double Width { get; set; } = 320;

    public bool IsVisible => _card.Superview is not null;

    /// <summary>The highlighted row, or null.</summary>
    public ShellSearchSuggestion? Highlighted => _highlighted >= 0 && _highlighted < _suggestions.Count ? _suggestions[_highlighted] : null;

    /// <summary>Shows <paramref name="suggestions"/> under <paramref name="anchor"/>; an empty list closes it.</summary>
    public void Show(UIView anchor, IReadOnlyList<ShellSearchSuggestion> suggestions)
    {
        if (_disposed) return;
        if (suggestions.Count == 0 || anchor.Window is not { } window) { Close(); return; }

        _suggestions = suggestions;
        _highlighted = -1;
        _table.ReloadData();
        if (_card.Superview != window) window.AddSubview(_card);

        var frame = anchor.ConvertRectToView(anchor.Bounds, window);
        var width = Math.Max(Width, (double)frame.Width);
        var height = Math.Min(MaximumHeight, suggestions.Sum(item => item.Subtitle is null ? CompactRowHeight : RowHeight) + 8);
        var x = Math.Clamp((double)frame.GetMidX() - width / 2, 8, Math.Max(8, (double)window.Bounds.Width - width - 8));
        _card.Frame = new CGRect(x, frame.GetMaxY() + 6, width, height);
        window.BringSubviewToFront(_card);
    }

    /// <summary>Moves the highlight by <paramref name="delta"/> rows (wrapping). False when the list is closed.</summary>
    public bool Move(int delta)
    {
        if (!IsVisible || _suggestions.Count == 0) return false;
        int next = _highlighted < 0
            ? (delta > 0 ? 0 : _suggestions.Count - 1)
            : ((_highlighted + delta) % _suggestions.Count + _suggestions.Count) % _suggestions.Count;
        _highlighted = next;
        var path = NSIndexPath.FromRowSection(next, 0);
        _table.SelectRow(path, false, UITableViewScrollPosition.Middle);
        UIAccessibility.PostNotification(UIAccessibilityPostNotification.Announcement, new NSString(_suggestions[next].Title));
        return true;
    }

    /// <summary>Chooses the highlighted row. False when the list is closed or nothing is highlighted.</summary>
    public bool TryChoose()
    {
        if (Highlighted is not { } suggestion) return false;
        Choose(suggestion);
        return true;
    }

    public void Close()
    {
        if (!IsVisible) return;
        _card.RemoveFromSuperview();
        _highlighted = -1;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void Choose(ShellSearchSuggestion suggestion)
    {
        Close();
        Chosen?.Invoke(this, suggestion);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
        Chosen = null;
        Closed = null;
        _table.Source = null;
        _source.Dispose();
    }

    private sealed class SuggestionSource(ShellSearchSuggestionController owner) : UITableViewSource
    {
        private const string Identifier = "WinoSuggestion";

        public override nint RowsInSection(UITableView tableview, nint section) => owner._suggestions.Count;

        public override nfloat GetHeightForRow(UITableView tableView, NSIndexPath indexPath)
            => (nfloat)(owner._suggestions[indexPath.Row].Subtitle is null ? CompactRowHeight : RowHeight);

        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            var cell = tableView.DequeueReusableCell(Identifier) ?? new UITableViewCell(UITableViewCellStyle.Subtitle, Identifier);
            var suggestion = owner._suggestions[indexPath.Row];
            var content = UIListContentConfiguration.SubtitleCellConfiguration;
            content.Text = suggestion.Title;
            content.SecondaryText = suggestion.Subtitle;
            content.SecondaryTextProperties.Color = WinoStyle.SecondaryText;
            content.Image = suggestion.Image ?? (suggestion.Dot is { } dot ? Dot(dot) : null);
            cell.ContentConfiguration = content;
            cell.BackgroundColor = UIColor.Clear;
            return cell;
        }

        public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
        {
            if (indexPath.Row < owner._suggestions.Count) owner.Choose(owner._suggestions[indexPath.Row]);
        }

        private static UIImage Dot(UIColor color)
        {
            using var renderer = new UIGraphicsImageRenderer(new CGSize(12, 12));
            return renderer.CreateImage(context =>
            {
                color.SetFill();
                UIBezierPath.FromOval(new CGRect(1, 1, 10, 10)).Fill();
            });
        }
    }
}
