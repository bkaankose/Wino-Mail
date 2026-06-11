using System.Collections.Generic;
using System.Linq;
using System;
using Microsoft.CodeAnalysis;

namespace Wino.SourceGenerators.Rpc;

internal static class RpcModel
{
    public const string ServiceAttributeMetadataName = "Wino.Core.Domain.Attributes.WinoRpcServiceAttribute";
    public const string ExcludeAttributeMetadataName = "Wino.Core.Domain.Attributes.WinoRpcExcludeAttribute";
    public const string ContractsAssemblyName = "Wino.Ipc.Contracts";
    public const string GeneratedNamespace = "Wino.Ipc.Contracts.Generated";

    public static readonly SymbolDisplayFormat FullyQualified = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>
    /// Collects all named types from the current compilation and every referenced Wino assembly.
    /// </summary>
    public static IEnumerable<INamedTypeSymbol> GetAllWinoTypes(Compilation compilation)
    {
        foreach (var type in GetNamespaceTypes(compilation.Assembly.GlobalNamespace))
            yield return type;

        foreach (var reference in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            if (!reference.Name.StartsWith("Wino", StringComparison.Ordinal))
                continue;

            foreach (var type in GetNamespaceTypes(reference.GlobalNamespace))
                yield return type;
        }
    }

    private static IEnumerable<INamedTypeSymbol> GetNamespaceTypes(INamespaceSymbol namespaceSymbol)
    {
        foreach (var member in namespaceSymbol.GetMembers())
        {
            if (member is INamespaceSymbol childNamespace)
            {
                foreach (var type in GetNamespaceTypes(childNamespace))
                    yield return type;
            }
            else if (member is INamedTypeSymbol type)
            {
                yield return type;

                foreach (var nested in type.GetTypeMembers())
                    yield return nested;
            }
        }
    }

    public static bool HasAttribute(ISymbol symbol, string attributeMetadataName)
        => symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == attributeMetadataName);
}
