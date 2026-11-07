using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Wino.Intelligence.ConsoleApp.Voice;

/// <summary>
/// Keeps every case of a run on the same data: once <see cref="IsReadOnly"/> is set, the service's writes (methods
/// named Update, Delete, Insert, Create, Add, Upsert, Save, Remove, Set or Mark) are recorded as operations instead
/// of changing the data copy. Parallel cases would otherwise see each other's renamed or moved events.
/// </summary>
internal class ReadOnlyServiceProxy<T> : DispatchProxy where T : class
{
    private static readonly string[] WritePrefixes = ["Update", "Delete", "Insert", "Create", "Add", "Upsert", "Save", "Remove", "Set", "Mark"];

    public static bool IsReadOnly { get; set; }

    private T _inner = null!;

    public static T Wrap(T inner)
    {
        var proxy = Create<T, ReadOnlyServiceProxy<T>>();
        ((ReadOnlyServiceProxy<T>)(object)proxy)._inner = inner;
        return proxy;
    }

    public static void Decorate(IServiceCollection services)
    {
        var descriptor = services.Last(descriptor => descriptor.ServiceType == typeof(T));
        services.Remove(descriptor);
        services.AddSingleton(provider =>
        {
            var inner = descriptor.ImplementationInstance as T
                ?? descriptor.ImplementationFactory?.Invoke(provider) as T
                ?? (T)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
            return Wrap(inner);
        });
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (IsReadOnly && WritePrefixes.Any(prefix => targetMethod.Name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var first = args?.FirstOrDefault();
            OperationLog.Add("Local." + targetMethod.Name.Replace("Async", string.Empty),
                first is Wino.Core.Domain.Entities.Calendar.CalendarItem item ? Describer.Event(item) : Describer.Describe(first));
            return Completed(targetMethod.ReturnType);
        }

        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static object? Completed(Type returnType)
    {
        if (returnType == typeof(Task))
            return Task.CompletedTask;
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            var value = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType).Invoke(null, [value]);
        }

        return returnType.IsValueType && returnType != typeof(void) ? Activator.CreateInstance(returnType) : null;
    }
}
