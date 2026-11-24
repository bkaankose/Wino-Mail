using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using Windows.UI;

namespace Wino.Mail.WinUI.Controls.Calendar;

public sealed class CompositionCanvasDrawEventArgs(CompositionCanvasDrawingSession drawingSession) : EventArgs
{
    public CompositionCanvasDrawingSession DrawingSession { get; } = drawingSession;
}

/// <summary>
/// Draws the calendar grid as composition sprites: axis-aligned lines and filled rectangles, which is
/// all the grid needs. The grid used to be drawn with Win2D into GPU surfaces, and every draw into
/// those large surfaces left GPU memory behind, so paging the calendar or switching its view grew
/// the app's GPU memory without bound.
/// </summary>
public sealed partial class CompositionCanvas : Grid
{
    private readonly CompositionCanvasDrawingSession _session;
    private ContainerVisual? _root;
    private bool _isDrawQueued;
    private bool _isDisposed;

    public event Windows.Foundation.TypedEventHandler<CompositionCanvas, CompositionCanvasDrawEventArgs>? Draw;

    public CompositionCanvas()
    {
        _session = new CompositionCanvasDrawingSession();
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    public void Invalidate()
    {
        if (_isDrawQueued || _isDisposed || !IsLoaded)
        {
            return;
        }

        _isDrawQueued = true;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, DrawNow))
        {
            _isDrawQueued = false;
        }
    }

    /// <summary>Removes the drawing for good; the control draws nothing afterwards.</summary>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Draw = null;
        Loaded -= OnLoaded;
        SizeChanged -= OnSizeChanged;

        if (_root != null)
        {
            ElementCompositionPreview.SetElementChildVisual(this, null);
            _session.Release();
            _root.Dispose();
            _root = null;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Invalidate();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Invalidate();

    private void DrawNow()
    {
        _isDrawQueued = false;

        if (_isDisposed || !IsLoaded)
        {
            return;
        }

        if (_root is null)
        {
            var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            _root = compositor.CreateContainerVisual();
            _root.Clip = compositor.CreateInsetClip();
            ElementCompositionPreview.SetElementChildVisual(this, _root);
        }

        _root.Size = new Vector2((float)ActualWidth, (float)ActualHeight);
        _session.Begin(_root);
        Draw?.Invoke(this, new CompositionCanvasDrawEventArgs(_session));
        _session.End();
    }
}

/// <summary>
/// Records the lines and rectangles of one draw into a pool of sprite visuals. Lines are one pixel
/// wide and centred on their coordinate; rectangles stacked in the same column with the same colour
/// are merged into one sprite.
/// </summary>
public sealed class CompositionCanvasDrawingSession
{
    private const float LineWidth = 1f;

    private readonly List<SpriteVisual> _sprites = [];
    private readonly Dictionary<Color, CompositionColorBrush> _brushes = [];
    private ContainerVisual? _root;
    private int _used;
    private SpriteVisual? _lastFill;
    private Color _lastFillColor;

    internal void Begin(ContainerVisual root)
    {
        _root = root;
        _used = 0;
        _lastFill = null;
    }

    internal void End()
    {
        for (var index = _used; index < _sprites.Count; index++)
        {
            _sprites[index].IsVisible = false;
        }

        _root = null;
    }

    internal void Release()
    {
        foreach (var sprite in _sprites)
        {
            sprite.Dispose();
        }

        foreach (var brush in _brushes.Values)
        {
            brush.Dispose();
        }

        _sprites.Clear();
        _brushes.Clear();
        _used = 0;
        _lastFill = null;
    }

    /// <summary>Starts the drawing over. Only a transparent clear is supported, which is all the grid uses.</summary>
    public void Clear(Color color)
    {
        _used = 0;
        _lastFill = null;
    }

    public void DrawLine(float x0, float y0, float x1, float y1, Color color)
    {
        _lastFill = null;

        if (color.A == 0)
        {
            return;
        }

        if (x0 == x1)
        {
            Place(x0 - (LineWidth / 2f), Math.Min(y0, y1), LineWidth, Math.Abs(y1 - y0), color);
        }
        else if (y0 == y1)
        {
            Place(Math.Min(x0, x1), y0 - (LineWidth / 2f), Math.Abs(x1 - x0), LineWidth, color);
        }
    }

    public void FillRectangle(Rect rect, Color color)
        => FillRectangle((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height, color);

    public void FillRectangle(float x, float y, float width, float height, Color color)
    {
        if (color.A == 0 || width <= 0 || height <= 0)
        {
            _lastFill = null;
            return;
        }

        if (_lastFill != null &&
            _lastFillColor == color &&
            _lastFill.Offset.X == x &&
            _lastFill.Size.X == width &&
            Math.Abs((_lastFill.Offset.Y + _lastFill.Size.Y) - y) < 0.01f)
        {
            _lastFill.Size = new Vector2(width, (y + height) - _lastFill.Offset.Y);
            return;
        }

        _lastFill = Place(x, y, width, height, color);
        _lastFillColor = color;
    }

    private SpriteVisual Place(float x, float y, float width, float height, Color color)
    {
        if (_root is null)
        {
            throw new InvalidOperationException("The drawing session is only valid inside a Draw handler.");
        }

        SpriteVisual sprite;
        if (_used < _sprites.Count)
        {
            sprite = _sprites[_used];
        }
        else
        {
            sprite = _root.Compositor.CreateSpriteVisual();
            _root.Children.InsertAtTop(sprite);
            _sprites.Add(sprite);
        }

        _used++;
        sprite.Brush = GetBrush(color);
        sprite.Offset = new Vector3(x, y, 0f);
        sprite.Size = new Vector2(width, height);
        sprite.IsVisible = true;
        return sprite;
    }

    private CompositionColorBrush GetBrush(Color color)
    {
        if (!_brushes.TryGetValue(color, out var brush))
        {
            brush = _root!.Compositor.CreateColorBrush(color);
            _brushes[color] = brush;
        }

        return brush;
    }
}
