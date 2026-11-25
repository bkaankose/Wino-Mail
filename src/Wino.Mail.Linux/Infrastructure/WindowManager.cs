using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views;
using Wino.Mail.ViewModels.Lifecycle;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// The secondary windows by key (ordinal): <c>mail:&lt;uniqueId&gt;</c>, <c>file:&lt;path&gt;</c>, <c>composer:&lt;draftId&gt;</c>,
/// <c>event:&lt;id&gt;</c>, <c>whatsnew</c>, <c>onboarding</c>. A window leaves the registry when it closed. On stop every
/// window closes and its <see cref="SecondaryWindow.Closing"/> is awaited before the services go away. Members other
/// than the lifecycle hooks run on the main thread.
/// </summary>
internal sealed class WindowManager(IDispatcher dispatcher, WindowStateStore sizes) : IApplicationLifecycleHook
{
    private readonly Dictionary<string, SecondaryWindow> _windows = new(StringComparer.Ordinal);
    private readonly HashSet<SecondaryWindow> _hidden = [];

    public WindowStateStore Sizes => sizes;

    public int Count => _windows.Count;

    /// <summary>Brings an existing window up; false when none has that key.</summary>
    public bool TryPresent(string key)
    {
        if (Find(key) is not { } window) return false;
        window.Present();
        return true;
    }

    public void Add(string key, SecondaryWindow window)
    {
        if (_windows.TryGetValue(key, out var existing) && !ReferenceEquals(existing, window))
            throw new InvalidOperationException($"A window with the key '{key}' is open already.");
        _windows[key] = window;
        _ = RemoveWhenClosedAsync(key, window);
    }

    public void Remove(string key, SecondaryWindow window)
    {
        if (_windows.TryGetValue(key, out var existing) && ReferenceEquals(existing, window)) _windows.Remove(key);
        _hidden.Remove(window);
    }

    public SecondaryWindow? Find(string key) => _windows.TryGetValue(key, out var window) && !window.IsClosing ? window : null;

    /// <summary>One line per window: key, title, visible.</summary>
    public string[] Describe() => [.. _windows.Select(entry => $"{entry.Key}\t{entry.Value.Title}\t{entry.Value.Window.GetVisible()}")];

    public async Task CloseAllAsync()
    {
        Task[] closed = [];
        await dispatcher.ExecuteOnUIThread(() =>
        {
            var windows = _windows.Values.ToArray();
            foreach (var window in windows) window.Close();
            closed = [.. windows.Select(window => window.Closed)];
        });
        await Task.WhenAll(closed);
    }

    /// <summary>App lock (PL-01): hides every visible window; showing again presents only those it hid. Call it after unlock.</summary>
    public void SetHidden(bool hidden)
    {
        if (hidden)
        {
            foreach (var window in _windows.Values)
            {
                if (!window.Window.GetVisible() || window.IsClosing) continue;
                window.Window.SetVisible(false);
                _hidden.Add(window);
            }
            return;
        }

        foreach (var window in _hidden.ToArray()) window.Present();
        _hidden.Clear();
    }

    public Task ServicesReadyAsync() => Task.CompletedTask;

    public Task StoppingAsync(bool runtimeStarted) => CloseAllAsync();

    private async Task RemoveWhenClosedAsync(string key, SecondaryWindow window)
    {
        await window.Closed;
        await dispatcher.ExecuteOnUIThread(() => Remove(key, window));
    }
}
