using System.Reflection;
using System.Text;
using Wino.Core.Domain.Interfaces;
using Wino.Ipc.Contracts;
using Wino.Ipc.Serialization;
using Xunit;

namespace Wino.Ipc.Tests;

/// <summary>
/// The AOT type-safety gate for the RPC pipe. Computes every type that can cross the
/// UI ↔ companion boundary (generated request/response records, RPC return types,
/// forwarded IUIMessages, domain exception states) and verifies the source-generated
/// WinoIpcJsonContext can resolve each of them. When an RPC signature or message changes,
/// this test fails and prints the exact [JsonSerializable] lines to add to
/// Wino.Ipc.Serialization\WinoIpcJsonContext.cs.
/// </summary>
public class WinoIpcJsonContextCompletenessTests
{
    [Fact]
    public void EveryCrossingType_IsRegisteredInWinoIpcJsonContext()
    {
        var requiredTypes = ComputeCrossingTypes();
        var options = WinoIpcJsonContext.Default.Options;

        var missingTypes = requiredTypes
            .Where(type => !options.TryGetTypeInfo(type, out _))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();

        if (missingTypes.Count == 0)
            return;

        var message = new StringBuilder();
        message.AppendLine($"{missingTypes.Count} crossing type(s) are not registered in WinoIpcJsonContext.");
        message.AppendLine("Add the following lines to Wino.Ipc.Serialization\\WinoIpcJsonContext.cs:");
        message.AppendLine();

        foreach (var type in missingTypes)
        {
            message.AppendLine($"[JsonSerializable(typeof({FormatTypeName(type)}))]");
        }

        Assert.Fail(message.ToString());
    }

    /// <summary>
    /// Interface- or abstract-typed members serialize (declared members only) but can
    /// never be deserialized on the other side of the pipe — a guaranteed runtime
    /// failure that the STJ source generator does not flag at compile time. Walks the
    /// serialization metadata graph of every crossing type and rejects such members;
    /// fix by using the concrete type or marking the member [JsonIgnore].
    /// </summary>
    [Fact]
    public void CrossingTypeGraphs_HaveNoInterfaceOrAbstractTypedMembers()
    {
        var options = WinoIpcJsonContext.Default.Options;
        var visited = new HashSet<Type>();
        var violations = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var rootType in ComputeCrossingTypes())
        {
            Visit(rootType);
        }

        if (violations.Count > 0)
        {
            Assert.Fail(
                $"{violations.Count} member(s) of pipe-crossing types cannot be deserialized:\n" +
                string.Join('\n', violations));
        }

        void Visit(Type type)
        {
            if (!visited.Add(type))
                return;

            if (!options.TryGetTypeInfo(type, out var typeInfo) || typeInfo == null)
                return;

            if (typeInfo.ElementType != null)
            {
                CheckAndVisit(typeInfo.ElementType, $"{FormatTypeName(type)} (element)");

                if (typeInfo.KeyType != null)
                    CheckAndVisit(typeInfo.KeyType, $"{FormatTypeName(type)} (key)");

                return;
            }

            foreach (var property in typeInfo.Properties)
            {
                // [JsonIgnore]d properties still appear in source-generated metadata but
                // have no accessors; they never serialize and are not a wire concern.
                if (property.Get == null && property.Set == null)
                    continue;

                CheckAndVisit(property.PropertyType, $"{FormatTypeName(type)}.{property.Name}");
            }
        }

        void CheckAndVisit(Type memberType, string location)
        {
            // ApiEnvelope<T>.Details comes from the external Wino.Mail.Contracts package;
            // its runtime contract is JsonElement (deserialized API error details), which
            // round-trips correctly under source generation.
            if (location.EndsWith(">.Details", StringComparison.Ordinal) && memberType == typeof(object))
                return;

            var underlyingType = Nullable.GetUnderlyingType(memberType) ?? memberType;

            if (underlyingType.IsInterface && !IsSupportedCollectionInterface(underlyingType))
            {
                violations.Add($"{location} : {FormatTypeName(underlyingType)} (interface)");
                return;
            }

            if (underlyingType == typeof(object))
            {
                violations.Add($"{location} : object");
                return;
            }

            // Static classes are abstract+sealed and cannot appear as members anyway.
            if (underlyingType.IsClass && underlyingType.IsAbstract && !underlyingType.IsSealed)
            {
                violations.Add($"{location} : {FormatTypeName(underlyingType)} (abstract)");
                return;
            }

            Visit(underlyingType);
        }

        static bool IsSupportedCollectionInterface(Type type)
            => type.IsGenericType && type.Namespace == "System.Collections.Generic";
    }

    private static HashSet<Type> ComputeCrossingTypes()
    {
        var types = new HashSet<Type>();

        var contractsAssembly = typeof(WinoIpcJson).Assembly;
        var domainAssembly = typeof(IMailService).Assembly;
        var messagingAssembly = typeof(Wino.Messaging.UI.MailAddedMessage).Assembly;

        // 1. Every generated request/response record.
        foreach (var type in contractsAssembly.GetTypes())
        {
            if (type.Namespace == "Wino.Ipc.Contracts.Generated" &&
                type.IsClass &&
                (type.Name.EndsWith("Request", StringComparison.Ordinal) || type.Name.EndsWith("Response", StringComparison.Ordinal)))
            {
                types.Add(type);
            }
        }

        // 2. Every non-excluded RPC method's unwrapped return type.
        foreach (var serviceInterface in domainAssembly.GetTypes().Where(IsRpcServiceInterface))
        {
            foreach (var method in serviceInterface.GetMethods())
            {
                // Skip property/event accessors; exclusion attributes live on the member itself.
                if (method.IsSpecialName)
                    continue;

                if (HasAttribute(method, "WinoRpcExcludeAttribute"))
                    continue;

                var returnValueType = UnwrapReturnType(method.ReturnType);

                if (returnValueType != null)
                    types.Add(returnValueType);
            }
        }

        // 3. Every forwarded UI message.
        foreach (var assembly in new[] { messagingAssembly, domainAssembly })
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsClass && !type.IsAbstract && !type.IsGenericTypeDefinition &&
                    type.IsPublic && typeof(IUIMessage).IsAssignableFrom(type))
                {
                    types.Add(type);
                }
            }
        }

        // 4. Domain exception wire states.
        types.Add(typeof(InteractiveAuthRequiredState));
        types.Add(typeof(UnavailableSpecialFolderState));
        types.Add(typeof(InvalidMoveTargetState));

        return types;
    }

    private static bool IsRpcServiceInterface(Type type)
        => type.IsInterface && HasAttribute(type, "WinoRpcServiceAttribute");

    private static bool HasAttribute(MemberInfo member, string attributeName)
        => member.GetCustomAttributes(inherit: false).Any(a => a.GetType().Name == attributeName);

    /// <summary>
    /// Task&lt;T&gt; → T; Task/void → null (no payload); tuples → null (the generated
    /// response record covers them and is registered through the record scan).
    /// </summary>
    private static Type? UnwrapReturnType(Type returnType)
    {
        if (returnType == typeof(void) || returnType == typeof(Task))
            return null;

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            returnType = returnType.GetGenericArguments()[0];

        if (returnType.FullName?.StartsWith("System.ValueTuple", StringComparison.Ordinal) == true)
            return null;

        return returnType;
    }

    private static string FormatTypeName(Type type)
    {
        if (type.IsArray)
            return $"{FormatTypeName(type.GetElementType()!)}[]";

        if (type.IsGenericType)
        {
            var definitionName = type.GetGenericTypeDefinition().FullName!;
            definitionName = definitionName[..definitionName.IndexOf('`')];
            var arguments = string.Join(", ", type.GetGenericArguments().Select(FormatTypeName));
            return $"global::{definitionName}<{arguments}>";
        }

        return type == typeof(bool) ? "bool"
            : type == typeof(int) ? "int"
            : type == typeof(string) ? "string"
            : $"global::{type.FullName}";
    }
}
