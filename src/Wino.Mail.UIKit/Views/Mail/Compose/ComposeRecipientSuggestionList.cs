using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Mail.Compose;

/// <summary>
/// Recipient suggestions under the active To / Cc / Bcc field (Mac ComposeRecipientSuggestionPopup, Windows
/// RecipientSuggestionTemplate) as an overlay list: picture, name with the matched text in semibold, address or member
/// count, the source caption and, for remembered correspondents, "Don't suggest", which keeps the list open. The page
/// places it under the field; the caret stays in the field. Main thread only.
/// </summary>
internal sealed class ComposeRecipientSuggestionList : WinoSurfaceView
{
    private const double RowHeight = 56;
    private const int MaximumRows = 5;
    private readonly IPictureStorageService? _pictures;
    private readonly Dictionary<Guid, UIImage?> _pictureCache = [];
    private readonly UITableView _table = new(CoreGraphics.CGRect.Empty, UITableViewStyle.Plain);
    private readonly NSLayoutConstraint _height;
    private readonly Source _source;
    private List<RecipientSuggestion> _items = [];
    private string _query = string.Empty;

    public ComposeRecipientSuggestionList(IPictureStorageService? pictures)
    {
        _pictures = pictures;
        Fill = UIColor.SecondarySystemGroupedBackground;
        Stroke = WinoStyle.ZoneStroke;
        CornerRadius = WinoStyle.PopoverRadius;
        Hidden = true;
        _table.TranslatesAutoresizingMaskIntoConstraints = false;
        _table.BackgroundColor = UIColor.Clear;
        _table.RowHeight = (nfloat)RowHeight;
        _table.KeyboardDismissMode = UIScrollViewKeyboardDismissMode.None;
        _table.SeparatorInset = new UIEdgeInsets(0, 56, 0, 0);
        _table.RegisterClassForCellReuse(typeof(SuggestionCell), SuggestionCell.CellId);
        _source = new Source(this);
        _table.Source = _source;
        _table.AccessibilityLabel = Translator.ComposerTo;
        WinoLayout.Fill(_table, this);
        _height = HeightAnchor.ConstraintEqualTo((nfloat)RowHeight);
        _height.Active = true;
    }

    /// <summary>A row was tapped.</summary>
    public event EventHandler<RecipientSuggestion>? Picked;

    /// <summary>"Don't suggest" on a remembered correspondent.</summary>
    public event EventHandler<RecipientSuggestion>? SuppressRequested;

    public bool IsVisible => !Hidden;

    public IReadOnlyList<RecipientSuggestion> Items => _items;

    public string Query => _query;

    /// <summary>The field the list belongs to while it is open.</summary>
    public ComposeRecipientField? Anchor { get; private set; }

    /// <summary>The first row, which Return commits.</summary>
    public RecipientSuggestion? Highlighted => _items.Count > 0 ? _items[0] : null;

    /// <summary>Shows <paramref name="items"/> for <paramref name="anchor"/>; empty closes.</summary>
    public void Show(ComposeRecipientField anchor, IReadOnlyList<RecipientSuggestion> items, string query)
    {
        if (items.Count == 0)
        {
            Close();
            return;
        }
        _items = items.ToList();
        _query = query;
        Anchor = anchor;
        _height.Constant = (nfloat)(Math.Min(_items.Count, MaximumRows) * RowHeight);
        _table.AccessibilityLabel = anchor.Caption;
        _table.ReloadData();
        _table.ContentOffset = CoreGraphics.CGPoint.Empty;
        Hidden = false;
        Superview?.BringSubviewToFront(this);
        UIAccessibility.PostNotification(UIAccessibilityPostNotification.LayoutChanged, null);
    }

    public void Close()
    {
        Hidden = true;
        Anchor = null;
        _items = [];
    }

    /// <summary>Removes one row (after "Don't suggest") and keeps the list open.</summary>
    public void Remove(RecipientSuggestion suggestion)
    {
        var index = _items.IndexOf(suggestion);
        if (index < 0) return;
        _items.RemoveAt(index);
        if (_items.Count == 0) { Close(); return; }
        _height.Constant = (nfloat)(Math.Min(_items.Count, MaximumRows) * RowHeight);
        _table.ReloadData();
    }

    private UIImage? Picture(RecipientSuggestion suggestion)
    {
        if (_pictures is null || suggestion.ContactPictureFileId is not { } id) return null;
        if (_pictureCache.TryGetValue(id, out var cached)) return cached;
        UIImage? image = null;
        try
        {
            var path = _pictures.GetPicturePath(PictureKind.Contact, id);
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) image = UIImage.FromFile(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        _pictureCache[id] = image;
        return image;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Picked = null;
            SuppressRequested = null;
            _pictureCache.Clear();
        }
        base.Dispose(disposing);
    }

    private sealed class Source(ComposeRecipientSuggestionList owner) : UITableViewSource
    {
        public override nint RowsInSection(UITableView tableview, nint section) => owner._items.Count;

        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            var cell = (SuggestionCell)tableView.DequeueReusableCell(SuggestionCell.CellId, indexPath);
            var row = indexPath.Row;
            if (row < 0 || row >= owner._items.Count) return cell;
            var suggestion = owner._items[row];
            cell.Show(suggestion, owner._query, owner.Picture(suggestion), () => owner.SuppressRequested?.Invoke(owner, suggestion));
            return cell;
        }

        public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
        {
            tableView.DeselectRow(indexPath, false);
            var row = indexPath.Row;
            if (row >= 0 && row < owner._items.Count) owner.Picked?.Invoke(owner, owner._items[row]);
        }
    }

    private sealed class SuggestionCell : UITableViewCell
    {
        public const string CellId = "wino.compose.suggestion";
        private readonly WinoContactPicture _picture = new(32);
        private readonly WinoIconView _listGlyph = new(WinoIconGlyph.People, 20, WinoStyle.SecondaryText);
        private readonly UILabel _name = WinoStyle.Label(string.Empty, WinoStyle.Body);
        private readonly UILabel _secondary = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
        private readonly UILabel _sourceCaption = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.TertiaryText);
        private readonly UIButton _dismiss;
        private Action? _suppress;

        public SuggestionCell(ObjCRuntime.NativeHandle handle) : base(handle)
        {
            BackgroundColor = UIColor.Clear;
            var configuration = UIButtonConfiguration.PlainButtonConfiguration;
            configuration.Image = WinoIcons.Image(WinoIconGlyph.Dismiss, 12, null, Translator.RecipientSuggestion_DontSuggest);
            configuration.BaseForegroundColor = WinoStyle.SecondaryText;
            _dismiss = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false, AccessibilityLabel = Translator.RecipientSuggestion_DontSuggest };
            WinoLayout.Size(_dismiss, 36, 36);
            _dismiss.PrimaryActionTriggered += (_, _) => _suppress?.Invoke();
            _sourceCaption.SetContentCompressionResistancePriority(800, UILayoutConstraintAxis.Horizontal);
            _sourceCaption.SetContentHuggingPriority(800, UILayoutConstraintAxis.Horizontal);

            var avatar = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
            WinoLayout.Size(avatar, 32, 32);
            WinoLayout.Fill(_picture, avatar);
            _listGlyph.TranslatesAutoresizingMaskIntoConstraints = false;
            avatar.AddSubview(_listGlyph);
            NSLayoutConstraint.ActivateConstraints([_listGlyph.CenterXAnchor.ConstraintEqualTo(avatar.CenterXAnchor), _listGlyph.CenterYAnchor.ConstraintEqualTo(avatar.CenterYAnchor)]);

            var secondaryRow = WinoLayout.HStack(6, _secondary, _sourceCaption);
            var text = WinoLayout.VStack(1, _name, secondaryRow);
            text.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
            var row = WinoLayout.HStack(12, avatar, text, _dismiss);
            WinoLayout.Insets(row, 0, 12, 0, 6);
            WinoLayout.Fill(row, ContentView);
            IsAccessibilityElement = true;
            AccessibilityTraits = UIAccessibilityTrait.Button;
        }

        public void Show(RecipientSuggestion suggestion, string query, UIImage? picture, Action suppress)
        {
            _suppress = suppress;
            var name = string.IsNullOrWhiteSpace(suggestion.DisplayName) ? suggestion.Address ?? string.Empty : suggestion.DisplayName;
            _name.AttributedText = Highlight(name, query, WinoStyle.Body, WinoStyle.BodyStrong, WinoStyle.PrimaryText);
            var secondary = suggestion.SecondaryText ?? string.Empty;
            _secondary.AttributedText = Highlight(secondary, query, WinoStyle.Caption, WinoStyle.CaptionStrong, WinoStyle.SecondaryText);
            _sourceCaption.Text = suggestion.SourceCaption ?? string.Empty;
            _sourceCaption.Hidden = string.IsNullOrWhiteSpace(suggestion.SourceCaption);
            _listGlyph.Hidden = !suggestion.IsList;
            _picture.Hidden = suggestion.IsList;
            if (!suggestion.IsList)
            {
                _picture.SetIdentity(name, suggestion.Address);
                _picture.Image = picture;
            }
            _dismiss.Hidden = !suggestion.CanSuppress;
            AccessibilityLabel = string.Join(", ", new[] { name, secondary, suggestion.SourceCaption }.Where(static part => !string.IsNullOrWhiteSpace(part)));
            AccessibilityCustomActions = suggestion.CanSuppress
                ? [new UIAccessibilityCustomAction(Translator.RecipientSuggestion_DontSuggest, (Func<UIAccessibilityCustomAction, bool>)(_ => { _suppress?.Invoke(); return true; }))]
                : [];
        }

        /// <summary>The text with every match of <paramref name="query"/> in the strong font.</summary>
        private static NSAttributedString Highlight(string text, string query, UIFont font, UIFont strong, UIColor color)
        {
            var result = new NSMutableAttributedString(text, new UIStringAttributes { Font = font, ForegroundColor = color });
            if (string.IsNullOrWhiteSpace(query) || text.Length == 0) return result;
            var start = 0;
            while (start < text.Length)
            {
                var index = text.IndexOf(query, start, StringComparison.CurrentCultureIgnoreCase);
                if (index < 0) break;
                result.AddAttribute(UIStringAttributeKey.Font, strong, new NSRange(index, query.Length));
                start = index + query.Length;
            }
            return result;
        }
    }
}
