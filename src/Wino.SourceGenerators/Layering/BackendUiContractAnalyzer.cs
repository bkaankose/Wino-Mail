using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Wino.SourceGenerators.Layering;

/// <summary>
/// The backend below the ViewModels never asks the user, navigates, themes or dispatches. A question belongs to the
/// ViewModel that started the operation, which passes the answer in as data, and a notice goes out as a messenger
/// message. This keeps a backend operation from blocking on a screen and lets a host run it without one. Declaring the
/// contracts in <c>Wino.Core.Domain.Interfaces</c> stays allowed: interface members are not checked, only the fields,
/// properties and constructor or method parameters of classes, structs and records.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BackendUiContractAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "WINOUI001";

    private static readonly DiagnosticDescriptor UiContractInBackend = new(
        id: DiagnosticId,
        title: "Backend code takes a UI contract",
        messageFormat: "'{0}' takes the UI contract '{1}'. The backend never asks or shows anything: let the ViewModel ask and pass the answer on the request, return the outcome to the caller, or send a message through IMessenger.",
        category: "WinoLayering",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly ImmutableHashSet<string> BackendAssemblies = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "Wino.Core.Domain",
        "Wino.Services",
        "Wino.Core",
        "Wino.Authentication");

    private const string ContractNamespace = "Wino.Core.Domain.Interfaces";

    private static readonly ImmutableHashSet<string> UiContracts = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "IMailDialogService",
        "IDialogServiceBase",
        "IAccountCreationDialog",
        "ICreateAccountAliasDialog",
        "IMailPrintPresenter",
        "INavigationService",
        "INotificationResponseNavigator",
        "INewThemeService",
        "IUnderlyingThemeService",
        "IDispatcher",
        "IShellMenuProvider",
        "IMailShellClient",
        "ICalendarShellClient",
        "IShellViewModel",
        "IShellHost",
        "IShellMenuOwner",
        "IShellMenuProviderResolver",
        "IShellMenuSink");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(UiContractInBackend);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            if (start.Compilation.AssemblyName is not { } name || !BackendAssemblies.Contains(name)) return;

            start.RegisterSymbolAction(AnalyzeField, SymbolKind.Field);
            start.RegisterSymbolAction(AnalyzeProperty, SymbolKind.Property);
            start.RegisterSymbolAction(AnalyzeMethod, SymbolKind.Method);
        });
    }

    private static void AnalyzeField(SymbolAnalysisContext context)
    {
        var field = (IFieldSymbol)context.Symbol;
        if (field.IsImplicitlyDeclared || !IsImplementation(field.ContainingType)) return;

        Report(context, field, field.Type);
    }

    private static void AnalyzeProperty(SymbolAnalysisContext context)
    {
        var property = (IPropertySymbol)context.Symbol;
        if (!IsImplementation(property.ContainingType)) return;

        Report(context, property, property.Type);
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (!IsImplementation(method.ContainingType)) return;
        if (method.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove) return;

        foreach (var parameter in method.Parameters)
        {
            Report(context, parameter, parameter.Type, method);
        }
    }

    private static bool IsImplementation(INamedTypeSymbol? type)
        => type is { TypeKind: TypeKind.Class or TypeKind.Struct };

    private static void Report(SymbolAnalysisContext context, ISymbol symbol, ITypeSymbol type, IMethodSymbol? owner = null)
    {
        if (FindUiContract(type) is not { } contract) return;

        var location = Location.None;
        if (symbol.Locations.Length > 0 && symbol.Locations[0].IsInSource)
            location = symbol.Locations[0];
        else if (owner is not null && owner.Locations.Length > 0)
            location = owner.Locations[0];

        var name = owner is null ? symbol.ToDisplayString() : $"{owner.ToDisplayString()} ({symbol.Name})";
        context.ReportDiagnostic(Diagnostic.Create(UiContractInBackend, location, name, contract.Name));
    }

    /// <summary>The contract itself, one it derives from, or one carried as a type argument or array element (Lazy&lt;IMailDialogService&gt;).</summary>
    private static INamedTypeSymbol? FindUiContract(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return FindUiContract(array.ElementType);
            case INamedTypeSymbol named:
                if (IsUiContract(named)) return named;

                foreach (var implemented in named.AllInterfaces)
                {
                    if (IsUiContract(implemented)) return implemented;
                }

                foreach (var argument in named.TypeArguments)
                {
                    if (FindUiContract(argument) is { } found) return found;
                }

                return null;
            default:
                return null;
        }
    }

    private static bool IsUiContract(INamedTypeSymbol type)
        => type.TypeKind == TypeKind.Interface
           && UiContracts.Contains(type.Name)
           && type.ContainingNamespace?.ToDisplayString() == ContractNamespace;
}
