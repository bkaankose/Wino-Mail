namespace Wino.Presentation.Linux;

/// <summary>Scheduling on the GTK main loop. Every member must be called on the main thread.</summary>
public static class MainLoop
{
    public static void Post(Action action)
        => GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT, () =>
        {
            action();
            return false;
        });

    /// <summary>Runs <paramref name="action"/> once after <paramref name="milliseconds"/>; dispose cancels it.</summary>
    public static IDisposable Delay(uint milliseconds, Action action)
    {
        var timer = new Throttle(milliseconds, action);
        timer.Request();
        return timer;
    }

    /// <summary>Runs its action once per main-loop turn, however often it was requested in that turn.</summary>
    public sealed class Coalescer(Action action) : IDisposable
    {
        private uint _source;
        private bool _disposed;

        public void Request()
        {
            if (_disposed || _source != 0) return;
            _source = GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT_IDLE, () =>
            {
                // The id is cleared before returning false: removing a finished source logs a GLib critical.
                _source = 0;
                if (!_disposed) action();
                return false;
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_source == 0) return;
            GLib.Functions.SourceRemove(_source);
            _source = 0;
        }
    }

    /// <summary>A trailing debounce: the action runs <c>milliseconds</c> after the last request.</summary>
    public sealed class Throttle(uint milliseconds, Action action) : IDisposable
    {
        private uint _source;
        private bool _disposed;

        public void Request()
        {
            if (_disposed) return;
            Cancel();
            _source = GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, milliseconds, () =>
            {
                _source = 0;
                if (!_disposed) action();
                return false;
            });
        }

        public void Flush()
        {
            if (_disposed || _source == 0) return;
            Cancel();
            action();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Cancel();
        }

        private void Cancel()
        {
            if (_source == 0) return;
            GLib.Functions.SourceRemove(_source);
            _source = 0;
        }
    }
}
