using Android.Graphics;
using Android.OS;
using Android.Views;
using AndroidX.Core.View.Accessibility;
using AndroidX.CustomView.Widget;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.Controls.Android.Calendar;

public sealed partial class WinoCalendarSurfaceView
{
    /// <summary>A drawn tile as TalkBack sees it: the item, its day and its bounds in the host view's pixels.</summary>
    private readonly record struct AccessibleTile(ICalendarItem Item, DateOnly Date, RectF Bounds);

    /// <summary>
    /// TalkBack nodes for the tiles a canvas view draws (the all-day lanes and the 24-hour grid): each visible tile is a
    /// virtual button named by its title and detail (time, location), as the iOS item views are, and activating it opens
    /// the item like a tap.
    /// </summary>
    private sealed class TileAccessibility : ExploreByTouchHelper
    {
        private readonly View _host;
        private readonly WinoCalendarSurfaceView _owner;
        private readonly Func<IReadOnlyList<AccessibleTile>> _tiles;

        public TileAccessibility(View host, WinoCalendarSurfaceView owner, Func<IReadOnlyList<AccessibleTile>> tiles) : base(host)
        {
            _host = host;
            _owner = owner;
            _tiles = tiles;
        }

        /// <summary>The visible tiles: clipped to the columns right of the hour gutter.</summary>
        private IReadOnlyList<AccessibleTile> Visible()
        {
            float gutter = _owner.GutterPx;
            var tiles = _tiles();
            var visible = new List<AccessibleTile>(tiles.Count);
            foreach (var tile in tiles)
                if (tile.Bounds.Right > gutter && tile.Bounds.Left < _host.Width && tile.Bounds.Width() > 0 && tile.Bounds.Height() > 0) visible.Add(tile);
            return visible;
        }

        protected override int GetVirtualViewAt(float x, float y)
        {
            var tiles = Visible();
            for (int index = tiles.Count - 1; index >= 0; index--)
                if (tiles[index].Bounds.Contains(x, y)) return index;
            return InvalidId;
        }

        protected override void GetVisibleVirtualViews(IList<Java.Lang.Integer>? virtualViewIds)
        {
            if (virtualViewIds is null) return;
            int count = Visible().Count;
            for (int index = 0; index < count; index++) virtualViewIds.Add(Java.Lang.Integer.ValueOf(index));
        }

        protected override void OnPopulateNodeForVirtualView(int virtualViewId, AccessibilityNodeInfoCompat node)
        {
            var tiles = Visible();
            if (virtualViewId < 0 || virtualViewId >= tiles.Count)
            {
                // A stale id after the tiles changed: the node still needs bounds and a description.
                node.ContentDescription = string.Empty;
                node.SetBoundsInParent(new Rect(0, 0, 1, 1));
                return;
            }
            var tile = tiles[virtualViewId];
            var model = _owner.Tile(tile.Item, tile.Date);
            node.ContentDescription = string.IsNullOrWhiteSpace(model.Detail) ? model.Title : $"{model.Title}, {model.Detail}";
            node.ClassName = "android.widget.Button";
            node.Clickable = true;
            node.AddAction(AccessibilityNodeInfoCompat.ActionClick);
            node.Selected = model.IsSelected;
            var bounds = new Rect();
            tile.Bounds.RoundOut(bounds);
            bounds.Left = Math.Max(bounds.Left, (int)_owner.GutterPx);
            bounds.Right = Math.Min(bounds.Right, _host.Width);
            node.SetBoundsInParent(bounds);
        }

        protected override bool OnPerformActionForVirtualView(int virtualViewId, int action, Bundle? arguments)
        {
            if (action != AccessibilityNodeInfoCompat.ActionClick) return false;
            var tiles = Visible();
            if (virtualViewId < 0 || virtualViewId >= tiles.Count) return false;
            var tile = tiles[virtualViewId];
            _owner.ItemClicked?.Invoke(_owner, new WinoCalendarItemEventArgs(tile.Item, _host, tile.Bounds.CenterX(), tile.Bounds.CenterY()));
            return true;
        }
    }
}
