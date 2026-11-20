using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Creates views in a DI scope of their own and disposes it with the view: a transient disposable resolved from the
/// root provider stays tracked by it until the app exits, so every ViewModel a view held would stay rooted. Views are
/// constructed here rather than registered in the container (unlike macOS), so adding a view needs no registration.
/// </summary>
internal static class PageScopes
{
    private static readonly ConditionalWeakTable<object, IServiceScope> _scopes = new();

    public static T Create<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(IServiceProvider services) where T : class
    {
        var scope = services.CreateScope();
        try
        {
            var instance = ActivatorUtilities.CreateInstance<T>(scope.ServiceProvider);
            _scopes.AddOrUpdate(instance, scope);
            return instance;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    /// <summary>Disposes the view's scope, and with it every disposable its construction resolved.</summary>
    public static void Release(object instance)
    {
        if (!_scopes.TryGetValue(instance, out var scope)) return;
        _scopes.Remove(instance);
        scope.Dispose();
    }
}
