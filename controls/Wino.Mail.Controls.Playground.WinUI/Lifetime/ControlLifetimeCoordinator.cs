using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Wino.Mail.Controls.Playground.Lifetime;

internal sealed class ControlLifetimeCoordinator(Action<string> trace)
{
    private static readonly HashSet<string> TrackedAssemblies =
    [
        "Wino.Mail.Controls",
        "Wino.Editor",
    ];

    private readonly Action<string> _trace = trace;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public async ValueTask<LifetimeSnapshot?> ReplacePageAsync(Frame frame, Type pageType)
    {
        if (frame.CurrentSourcePageType == pageType)
        {
            return null;
        }

        var outgoingPage = frame.Content as Page;
        var trackedObjects = outgoingPage is null ? [] : DiscoverTrackedObjects(outgoingPage);
        var snapshot = outgoingPage is null ? null : CreateSnapshot(outgoingPage, trackedObjects);

        // Detach the old tree before creating the next one. This avoids Frame's
        // transition machinery temporarily owning both pages during the check.
        frame.Content = null;
        frame.BackStack.Clear();
        frame.ForwardStack.Clear();

        await ReleasePageAsync(outgoingPage, trackedObjects);
        outgoingPage = null;
        trackedObjects.Clear();

        if (!frame.Navigate(pageType, null, new SuppressNavigationTransitionInfo()))
        {
            return null;
        }

        if (frame.Content is Page incomingPage)
        {
            incomingPage.NavigationCacheMode = NavigationCacheMode.Disabled;
        }

        frame.BackStack.Clear();
        frame.ForwardStack.Clear();
        return snapshot;
    }

    public void TraceLoaded(Page page)
    {
        var controls = DiscoverTrackedObjects(page);
        Write($"Loaded {page.GetType().Name}: {controls.Count} Wino control instance(s).");

        foreach (var control in controls)
        {
            Write($"  active {Describe(control)}");
        }
    }

    public async Task<LifetimeCheckResult> VerifyReleasedAsync(LifetimeSnapshot snapshot, CancellationToken cancellationToken)
    {
        IReadOnlyList<LifetimeReference> retained = snapshot.References;
        var containsWebView = snapshot.References.Any(static item =>
            item.Description.StartsWith("WinoMailEditor", StringComparison.Ordinal) ||
            item.Description.StartsWith("WinoMailRenderer", StringComparison.Ordinal));

        // Let the remaining Unloaded handlers finish before asking the GC to inspect
        // reachability. Async disposable controls have already completed above.
        await Task.Delay(150, cancellationToken);

        for (var attempt = 0; attempt < 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (containsWebView)
            {
                InduceCollectionPressure();
            }
            else
            {
                RequestFullCollection();
            }
            await Task.Delay(400, cancellationToken);
            retained = snapshot.References.Where(static item => item.Reference.IsAlive).ToArray();
            if (retained.Count == 0)
            {
                Write($"Collected {snapshot.PageName} and all {snapshot.ControlCount} tracked control instance(s).");
                return new(snapshot.PageName, snapshot.ControlCount, []);
            }

        }

        foreach (var item in retained)
        {
            Write($"RETAINED {snapshot.PageName}: {item.Description}");
        }

        return new(snapshot.PageName, snapshot.ControlCount, retained.Select(static item => item.Description).ToArray());
    }

    private static LifetimeSnapshot CreateSnapshot(Page page, IReadOnlyList<object> controls)
    {
        var references = new List<LifetimeReference>(controls.Count + 1)
        {
            new(new WeakReference(page), page.GetType().Name),
        };

        references.AddRange(controls.Select(static control => new LifetimeReference(new WeakReference(control), Describe(control))));
        return new(page.GetType().Name, controls.Count, references);
    }

    private static List<object> DiscoverTrackedObjects(Page page)
    {
        var discovered = new List<object>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        DiscoverVisualTree(page, discovered, seen);

        if (page is IPlaygroundLifetimeAware lifetimeAware)
        {
            foreach (var item in lifetimeAware.AdditionalLifetimeObjects)
            {
                if (item is not null && seen.Add(item))
                {
                    discovered.Add(item);
                }
            }
        }

        return discovered;
    }

    private static void DiscoverVisualTree(DependencyObject root, ICollection<object> discovered, ISet<object> seen)
    {
        if (IsTracked(root) && seen.Add(root))
        {
            discovered.Add(root);
        }

        if (root is FrameworkElement element)
        {
            AddFlyout(element.ContextFlyout, discovered, seen);
            AddFlyout(FlyoutBase.GetAttachedFlyout(element), discovered, seen);
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            DiscoverVisualTree(VisualTreeHelper.GetChild(root, index), discovered, seen);
        }
    }

    private static void AddFlyout(FlyoutBase? flyout, ICollection<object> discovered, ISet<object> seen)
    {
        if (flyout is not null && IsTracked(flyout) && seen.Add(flyout))
        {
            discovered.Add(flyout);
        }
    }

    private static bool IsTracked(object item) => TrackedAssemblies.Contains(item.GetType().Assembly.GetName().Name ?? string.Empty);

    private async ValueTask ReleasePageAsync(Page? page, IReadOnlyList<object> trackedObjects)
    {
        if (page is IDisposable disposablePage)
        {
            disposablePage.Dispose();
            Write($"Disposed {page.GetType().Name}.");
        }

        for (var index = trackedObjects.Count - 1; index >= 0; index--)
        {
            switch (trackedObjects[index])
            {
                case IAsyncDisposable asyncDisposable:
                    Write($"Disposal requested for {Describe(trackedObjects[index])}.");
                    await asyncDisposable.DisposeAsync();
                    Write($"Disposal completed for {Describe(trackedObjects[index])}.");
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    Write($"Disposed {Describe(trackedObjects[index])}.");
                    break;
                case FlyoutBase flyout:
                    flyout.Hide();
                    break;
            }
        }
    }

    private static string Describe(object item)
    {
        var typeName = item.GetType().Name;
        if (item is not DependencyObject dependencyObject)
        {
            return typeName;
        }

        var automationId = AutomationProperties.GetAutomationId(dependencyObject);
        if (!string.IsNullOrWhiteSpace(automationId))
        {
            return $"{typeName} [{automationId}]";
        }

        return item is FrameworkElement { Name.Length: > 0 } element
            ? $"{typeName} [{element.Name}]"
            : typeName;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RequestFullCollection()
    {
        // Request a background full collection. A page can be promoted while it is
        // active during the preceding route's check, so an ephemeral-only collection
        // would report a false retention.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InduceCollectionPressure()
    {
        // Explicit GC.Collect calls can deadlock WinUI while WebView2 releases its
        // native controller. Allocation pressure lets the runtime schedule the same
        // ephemeral collections at a safe point without keeping the tested page alive.
        for (var index = 0; index < 16; index++)
        {
            var buffer = GC.AllocateUninitializedArray<byte>(1024 * 1024);
            buffer[0] = (byte)index;
            GC.KeepAlive(buffer);
        }
    }

    private void Write(string message)
    {
        Debug.WriteLine($"[Control lifetime] {message}");
        _trace(message);
    }
}

internal sealed record LifetimeSnapshot(string PageName, int ControlCount, IReadOnlyList<LifetimeReference> References);

internal sealed record LifetimeReference(WeakReference Reference, string Description);

internal sealed record LifetimeCheckResult(string PageName, int ControlCount, IReadOnlyList<string> RetainedObjects)
{
    public bool Passed => RetainedObjects.Count == 0;
}
