using Android.Views;
using AndroidX.Core.View;

namespace Wino.Presentation.Android;

/// <summary>
/// Edge to edge: pages draw under the status and navigation bars and keep their content clear of them with padding
/// from the window insets (system bars and the display cutout).
/// </summary>
public static class WinoInsets
{
    /// <summary>Pads <paramref name="view"/> by the system bar and cutout insets on every inset change.</summary>
    /// <remarks>
    /// <paramref name="keyboardChanged"/> is called with the keyboard's visibility each time it changes, straight from
    /// the inset dispatch, so callers do not depend on a layout pass to notice the keyboard opening or closing.
    /// </remarks>
    public static void Apply(View view, bool top = true, bool bottom = true, Action<bool>? keyboardChanged = null)
    {
        ViewCompat.SetOnApplyWindowInsetsListener(view, new Listener(top, bottom, includeIme: false, keyboardChanged));
        ViewCompat.RequestApplyInsets(view);
    }

    /// <summary>Like <see cref="Apply"/>, and the bottom padding also clears the on-screen keyboard (forms).</summary>
    public static void ApplyWithKeyboard(View view, bool top = true)
    {
        ViewCompat.SetOnApplyWindowInsetsListener(view, new Listener(top, bottom: true, includeIme: true));
        ViewCompat.RequestApplyInsets(view);
    }

    /// <summary>
    /// Keeps the foot of <paramref name="view"/> (a page whose last child is a pinned bar, such as the To Do add-task bar)
    /// above the on-screen keyboard: its bottom padding grows by the part of the keyboard that overlaps the page, whatever
    /// sits below it (the compact navigation bar, the system bars). The view keeps its size; its content shrinks.
    /// </summary>
    public static void ApplyKeyboardLift(View view)
    {
        ViewCompat.SetOnApplyWindowInsetsListener(view, new KeyboardLift());
        ViewCompat.RequestApplyInsets(view);
    }

    private sealed class KeyboardLift : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        private int _basePadding = -1;
        private bool _keyboard;
        private View? _view;

        public WindowInsetsCompat OnApplyWindowInsets(View view, WindowInsetsCompat insets)
        {
            if (_basePadding < 0) _basePadding = view.PaddingBottom;
            if (_view is null)
            {
                // The window may resize for the keyboard after the insets arrive; measure again after each layout.
                _view = view;
                view.LayoutChange += (_, _) => view.Post(Update);
            }
            _keyboard = insets.IsVisible(WindowInsetsCompat.Type.Ime());
            Update();
            return insets;
        }

        private void Update()
        {
            if (_view is not { } view || _basePadding < 0) return;
            int lift = 0;
            if (_keyboard)
            {
                // The visible frame excludes the keyboard whether or not the window was resized for it.
                var visible = new global::Android.Graphics.Rect();
                view.GetWindowVisibleDisplayFrame(visible);
                var location = new int[2];
                view.GetLocationOnScreen(location);
                lift = Math.Max(0, location[1] + view.Height - visible.Bottom);
            }
            int padding = _basePadding + lift;
            if (view.PaddingBottom != padding) view.SetPadding(view.PaddingLeft, view.PaddingTop, view.PaddingRight, padding);
        }
    }

    private sealed class Listener(bool top, bool bottom, bool includeIme, Action<bool>? keyboardChanged = null)
        : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        private bool? _keyboard;

        public WindowInsetsCompat OnApplyWindowInsets(View view, WindowInsetsCompat insets)
        {
            if (keyboardChanged is not null)
            {
                bool keyboard = insets.IsVisible(WindowInsetsCompat.Type.Ime());
                if (keyboard != _keyboard)
                {
                    _keyboard = keyboard;
                    keyboardChanged(keyboard);
                }
            }
            int types = WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout();
            if (includeIme) types |= WindowInsetsCompat.Type.Ime();
            var bars = insets.GetInsets(types);
            view.SetPadding(bars.Left, top ? bars.Top : 0, bars.Right, bottom ? bars.Bottom : 0);
            return insets;
        }
    }
}
