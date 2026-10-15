using Android.Views;
using AndroidX.Lifecycle;
using Wino.Presentation.Bindings;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace Wino.Presentation.Android.Bindings;

/// <summary>
/// Ties a <see cref="BindingScope"/> to an Android lifetime, so bindings never outlive the views they write to:
/// a fragment's view lifecycle (created in OnViewCreated, disposed when the view is destroyed, including on
/// configuration changes) or a view's attachment to its window (dialogs, sheets, views built in code).
/// Recycled list rows do not use either; their adapter disposes the row's scope on rebind and recycle.
/// </summary>
public static class BindingLifetime
{
    /// <summary>
    /// A new scope disposed when <paramref name="fragment"/>'s current view is destroyed. Call from OnViewCreated.
    /// </summary>
    public static BindingScope ForViewLifecycle(Fragment fragment)
    {
        var scope = new BindingScope();
        var owner = fragment.ViewLifecycleOwner;
        owner.Lifecycle.AddObserver(new DestroyObserver(scope));
        return scope;
    }

    /// <summary>A new scope disposed the first time <paramref name="view"/> detaches from its window.</summary>
    public static BindingScope UntilDetached(View view)
    {
        var scope = new BindingScope();
        view.AddOnAttachStateChangeListener(new DetachListener(scope));
        return scope;
    }

    private sealed class DestroyObserver(BindingScope scope) : Java.Lang.Object, ILifecycleEventObserver
    {
        public void OnStateChanged(ILifecycleOwner source, Lifecycle.Event e)
        {
            if (e != Lifecycle.Event.OnDestroy) return;
            scope.Dispose();
            source.Lifecycle.RemoveObserver(this);
        }
    }

    private sealed class DetachListener(BindingScope scope) : Java.Lang.Object, View.IOnAttachStateChangeListener
    {
        public void OnViewAttachedToWindow(View attachedView) { }

        public void OnViewDetachedFromWindow(View detachedView)
        {
            scope.Dispose();
            detachedView.RemoveOnAttachStateChangeListener(this);
        }
    }
}
