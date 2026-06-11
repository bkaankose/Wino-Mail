using System.Collections.Generic;
using System;
using Microsoft.CodeAnalysis;

namespace Wino.SourceGenerators.Rpc;

/// <summary>
/// Shallow serializability validation for RPC member signatures. This is the forcing
/// function that keeps non-serializable types (MimeKit, MailKit, menu item interfaces,
/// IRequestBase, …) from crossing the pipe. Declared types only; nested members of DTOs
/// are exercised by the integration tests instead.
/// </summary>
internal static class RpcSerializabilityChecker
{
    private static readonly HashSet<string> AllowedSystemTypes =
    [
        "System.Guid",
        "System.DateTime",
        "System.DateTimeOffset",
        "System.TimeSpan",
        "System.DateOnly",
        "System.TimeOnly",
        "System.Uri",
        "System.Version",
        "System.Text.Json.JsonElement",
    ];

    private static readonly HashSet<string> AllowedCollectionTypes =
    [
        "System.Collections.Generic.List<T>",
        "System.Collections.Generic.IList<T>",
        "System.Collections.Generic.IReadOnlyList<T>",
        "System.Collections.Generic.IEnumerable<T>",
        "System.Collections.Generic.ICollection<T>",
        "System.Collections.Generic.IReadOnlyCollection<T>",
        "System.Collections.Generic.HashSet<T>",
        "System.Collections.Generic.ISet<T>",
        "System.Collections.Generic.IReadOnlySet<T>",
        "System.Collections.Generic.Dictionary<TKey, TValue>",
        "System.Collections.Generic.IDictionary<TKey, TValue>",
        "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>",
        "System.Collections.Generic.KeyValuePair<TKey, TValue>",
        "System.Collections.Generic.SortedDictionary<TKey, TValue>",
    ];

    public static bool IsSerializable(ITypeSymbol type, bool isReturnPosition)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Char:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
            case SpecialType.System_String:
                return true;
        }

        if (type is IArrayTypeSymbol arrayType)
            return IsSerializable(arrayType.ElementType, isReturnPosition);

        if (type.TypeKind == TypeKind.Enum)
            return true;

        if (type is INamedTypeSymbol namedType)
        {
            // Nullable<T>
            if (namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                return IsSerializable(namedType.TypeArguments[0], isReturnPosition);

            // Tuples are allowed in return position only; they are wrapped in generated response records.
            if (namedType.IsTupleType)
            {
                if (!isReturnPosition)
                    return false;

                foreach (var element in namedType.TupleElements)
                {
                    if (!IsSerializable(element.Type, isReturnPosition))
                        return false;
                }

                return true;
            }

            if (namedType.IsGenericType)
            {
                var definition = namedType.OriginalDefinition.ToDisplayString();

                // Generic Wino DTOs (e.g. ApiEnvelope<T>) are treated like the collections:
                // the container must be a concrete class/struct and every argument serializable.
                if (AllowedCollectionTypes.Contains(definition) ||
                    (definition.StartsWith("Wino.", StringComparison.Ordinal) && namedType.TypeKind is TypeKind.Class or TypeKind.Struct && !namedType.IsAbstract))
                {
                    foreach (var typeArgument in namedType.TypeArguments)
                    {
                        if (!IsSerializable(typeArgument, isReturnPosition))
                            return false;
                    }

                    return true;
                }

                return false;
            }

            var fullName = namedType.ToDisplayString();

            if (AllowedSystemTypes.Contains(fullName))
                return true;

            // Concrete (non-abstract) Wino domain classes, structs and records are allowed.
            // Interfaces and abstract types force either a refactor or [WinoRpcExclude],
            // and foreign library types (MimeKit, MailKit, Graph, Google) never cross.
            if (fullName.StartsWith("Wino.", StringComparison.Ordinal))
            {
                return namedType.TypeKind is TypeKind.Class or TypeKind.Struct && !namedType.IsAbstract;
            }
        }

        return false;
    }
}
