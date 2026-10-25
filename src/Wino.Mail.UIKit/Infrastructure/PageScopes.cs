using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>
/// Resolves page controllers in a scope of their own. A transient IDisposable resolved from the root
/// provider is tracked by that provider until the app exits, so every page controller (and all the
/// views and ViewModels it holds) stayed rooted after navigation. The scope is disposed with the page.
/// </summary>
public static class PageScopes
{
    private static readonly ConditionalWeakTable<object, IServiceScope> _scopes = new();

    public static T Resolve<T>(IServiceProvider services) where T : class => (T)Resolve(services, typeof(T));

    public static object Resolve(IServiceProvider services, Type type)
    {
        var scope = services.CreateScope();
        var instance = scope.ServiceProvider.GetRequiredService(type);
        _scopes.AddOrUpdate(instance, scope);
        return instance;
    }

    /// <summary>Disposes the page's scope, and with it every disposable the page's resolution created.</summary>
    public static void Release(object instance)
    {
        if (!_scopes.TryGetValue(instance, out var scope)) return;
        _scopes.Remove(instance);
        scope.Dispose();
    }
}
