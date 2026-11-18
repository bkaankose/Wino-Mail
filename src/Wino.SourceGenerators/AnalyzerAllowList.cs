using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Wino.SourceGenerators;

/// <summary>
/// The only exemptions from the layering and shared-state rules. There is no pragma, NoWarn or severity override for
/// them: an exemption is an entry here with its reason, so every exception is visible in one place. Remove an entry
/// when its code is fixed.
/// </summary>
internal static class AnalyzerAllowList
{
    /// <summary>WINOUI001: "containing type -> contract", both by namespace and name.</summary>
    private static readonly ImmutableDictionary<string, string> UiContractUses = ImmutableDictionary.CreateRange(
        StringComparer.Ordinal,
        new[]
        {
            Entry("Wino.Core.Services.Assistant.AgentPlanExecutor -> Wino.Core.Services.Assistant.IAgentPlanHost",
                "Navigation contract below the ViewModels that the owner knows about; moves with the assistant executor."),
        });

    /// <summary>WINOMSG002: static members by containing type and name.</summary>
    private static readonly ImmutableDictionary<string, string> StaticStates = ImmutableDictionary.CreateRange(
        StringComparer.Ordinal,
        new[]
        {
            Entry("Wino.Core.Domain.Extensions.MailAccountReauthenticationExtensions.IsOfflineDemoMode",
                "Process-wide demo switch read by about 20 files; the owner keeps it until the demo mode moves into a service."),
            Entry("Wino.Providers.Exchange.MAPI.Transport.MapiTls.ServerCertificateValidation",
                "Certificate seam no head sets yet; becomes a MapiHttpTransport constructor argument passed from the synchronizer when one does."),
        });

    public static bool AllowsUiContract(ISymbol site, string contract)
    {
        for (var type = site as INamedTypeSymbol ?? site.ContainingType; type is not null; type = type.ContainingType)
        {
            if (UiContractUses.ContainsKey(FullName(type) + " -> " + contract)) return true;
        }

        return false;
    }

    public static bool AllowsStaticState(ISymbol member)
        => member.ContainingType is { } type && StaticStates.ContainsKey(FullName(type) + "." + member.Name);

    internal static string FullName(INamedTypeSymbol type)
    {
        var name = type.MetadataName;
        var root = type;
        while (root.ContainingType is { } outer)
        {
            name = outer.MetadataName + "." + name;
            root = outer;
        }

        return root.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." + name : name;
    }

    private static KeyValuePair<string, string> Entry(string symbol, string reason) => new(symbol, reason);
}
