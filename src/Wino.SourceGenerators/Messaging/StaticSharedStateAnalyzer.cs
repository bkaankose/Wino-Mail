using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Wino.SourceGenerators.Messaging;

/// <summary>
/// A host runs the shared backend once per instance, so state that one instance writes must not reach another. A
/// static member in shared code is a violation when it can be assigned after start-up (a static field that is not
/// readonly or const, or a static auto-property with a setter), or when it holds a service whatever its modifiers: a
/// service, the container or the messenger, or a collection, array or wrapper (Lazy&lt;T&gt;) of one. Constants,
/// readonly values, readonly lookup tables, regexes, JSON contexts, comparers, loggers, delegates that take a service
/// as a parameter, and process-wide locks keyed by a file or account stay allowed. A service is recognized by name,
/// because the container registrations of a shared assembly live in another assembly the analyzer cannot see.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StaticSharedStateAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "WINOMSG002";

    private static readonly DiagnosticDescriptor StaticSharedState = new(
        id: DiagnosticId,
        title: "Shared code keeps process-wide state",
        messageFormat: "'{0}' is static {1}, which every host in the process shares. Make it an instance member of a service registered in the container and take that service through the constructor.",
        category: "WinoMessaging",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly ImmutableHashSet<string> SharedAssemblies = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "Wino.Core.Domain",
        "Wino.Services",
        "Wino.Core",
        "Wino.Authentication",
        "Wino.Providers.Outlook",
        "Wino.Providers.Gmail",
        "Wino.Providers.Imap",
        "Wino.Providers.Exchange",
        "Wino.Providers.Dav",
        "Wino.Mail.ViewModels",
        "Wino.Hosting",
        "Wino.Agent.Runtime",
        "Wino.Core.ML");

    /// <summary>A Wino interface, or a Wino class that is not a record and not under a Models or Entities namespace, whose name ends in one of these is a service.</summary>
    private static readonly ImmutableArray<string> ServiceSuffixes = ImmutableArray.Create(
        "Service", "Manager", "Coordinator", "Synchronizer", "Processor", "Delegator", "Executor", "Registry", "Runtime");

    /// <summary>Framework types that are the container, a scope of it, or a messenger.</summary>
    private static readonly ImmutableHashSet<string> FrameworkServices = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "System.IServiceProvider",
        "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory",
        "Microsoft.Extensions.DependencyInjection.IServiceScope",
        "CommunityToolkit.Mvvm.Messaging.IMessenger",
        "CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger",
        "CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(StaticSharedState);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            if (start.Compilation.AssemblyName is not { } name || !SharedAssemblies.Contains(name)) return;

            start.RegisterSymbolAction(AnalyzeField, SymbolKind.Field);
            start.RegisterSymbolAction(AnalyzeProperty, SymbolKind.Property);
        });
    }

    private static void AnalyzeField(SymbolAnalysisContext context)
    {
        var field = (IFieldSymbol)context.Symbol;
        if (!field.IsStatic || field.IsConst || field.IsImplicitlyDeclared) return;

        Report(context, field, field.Type, assignable: !field.IsReadOnly);
    }

    private static void AnalyzeProperty(SymbolAnalysisContext context)
    {
        var property = (IPropertySymbol)context.Symbol;
        if (!property.IsStatic || property.IsIndexer) return;

        if (!IsAutoProperty(property, context.CancellationToken)) return;

        Report(context, property, property.Type, assignable: property.SetMethod is not null);
    }

    private static void Report(SymbolAnalysisContext context, ISymbol member, ITypeSymbol type, bool assignable)
    {
        string? kind = assignable ? "and assignable" : DescribeHeldState(type);
        if (kind is null || AnalyzerAllowList.AllowsStaticState(member)) return;

        var location = member.Locations.Length > 0 ? member.Locations[0] : Location.None;
        context.ReportDiagnostic(Diagnostic.Create(StaticSharedState, location, member.ToDisplayString(), kind));
    }

    private static string? DescribeHeldState(ITypeSymbol type)
    {
        return FindService(type) is { } service ? $"and holds the service '{service.Name}'" : null;
    }

    private static INamedTypeSymbol? FindService(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return FindService(array.ElementType);
            case INamedTypeSymbol { TypeKind: TypeKind.Delegate }:
                return null;
            case INamedTypeSymbol named:
                if (IsService(named)) return named;

                foreach (var argument in named.TypeArguments)
                {
                    if (FindService(argument) is { } found) return found;
                }

                return null;
            default:
                return null;
        }
    }

    private static bool IsService(INamedTypeSymbol type)
    {
        if (FrameworkServices.Contains(FullName(type))) return true;
        if (type.TypeKind is not (TypeKind.Interface or TypeKind.Class) || type.IsStatic) return false;
        if (type.ContainingAssembly?.Name is not { } assembly || !assembly.StartsWith("Wino.", StringComparison.Ordinal)) return false;

        if (type.TypeKind == TypeKind.Class && (type.IsRecord || IsDataNamespace(type.ContainingNamespace))) return false;

        var name = type.Name;
        return ServiceSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal));
    }

    /// <summary>A class under a Models or Entities namespace is data even when its name reads like a service (VideoConferenceService).</summary>
    private static bool IsDataNamespace(INamespaceSymbol? ns)
    {
        for (; ns is { IsGlobalNamespace: false }; ns = ns.ContainingNamespace)
        {
            if (ns.Name is "Models" or "Entities") return true;
        }

        return false;
    }

    private static bool IsAutoProperty(IPropertySymbol property, System.Threading.CancellationToken cancellationToken)
    {
        foreach (var reference in property.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax(cancellationToken) is not PropertyDeclarationSyntax declaration) continue;
            if (declaration.ExpressionBody is not null || declaration.AccessorList is null) return false;

            return declaration.AccessorList.Accessors.All(accessor => accessor.Body is null && accessor.ExpressionBody is null);
        }

        return false;
    }

    private static string FullName(INamedTypeSymbol type) => AnalyzerAllowList.FullName(type);
}
