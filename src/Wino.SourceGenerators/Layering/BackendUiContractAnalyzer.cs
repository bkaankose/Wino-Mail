using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Wino.SourceGenerators.Layering;

/// <summary>
/// The backend below the ViewModels never asks the user, navigates, themes or dispatches. A question belongs to the
/// ViewModel that started the operation, which passes the answer in as data, and a notice goes out as a messenger
/// message. This keeps a backend operation from blocking on a screen and lets a host run it without one. Declaring the
/// contracts stays allowed: interface members are not checked. Everything a class, struct or record writes is: member
/// and local types, return types, type arguments, base types and constraints, service resolution
/// (<c>GetService&lt;T&gt;()</c>), object creation, casts, <c>typeof</c>, type patterns and member access on a contract.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BackendUiContractAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "WINOUI001";

    private static readonly DiagnosticDescriptor UiContractInBackend = new(
        id: DiagnosticId,
        title: "Backend code uses a UI contract",
        messageFormat: "'{0}' uses the UI contract '{1}'. The backend never asks or shows anything: let the ViewModel ask and pass the answer on the request, return the outcome to the caller, or send a message through IMessenger.",
        category: "WinoLayering",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly ImmutableHashSet<string> BackendAssemblies = ImmutableHashSet.Create(
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
        "Wino.Agent.Runtime",
        "Wino.Core.ML");

    /// <summary>The UI contracts, by namespace and name. Move an entry here when its contract moves.</summary>
    private static readonly ImmutableHashSet<string> UiContracts = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "Wino.Core.Domain.Interfaces.IMailDialogService",
        "Wino.Core.Domain.Interfaces.IDialogServiceBase",
        "Wino.Core.Domain.Interfaces.IAccountCreationDialog",
        "Wino.Core.Domain.Interfaces.ICreateAccountAliasDialog",
        "Wino.Core.Domain.Interfaces.IMailPrintPresenter",
        "Wino.Core.Domain.Interfaces.INavigationService",
        "Wino.Core.Domain.Interfaces.INotificationResponseNavigator",
        "Wino.Core.Domain.Interfaces.INewThemeService",
        "Wino.Core.Domain.Interfaces.IUnderlyingThemeService",
        "Wino.Core.Domain.Interfaces.IDispatcher",
        "Wino.Core.Services.Assistant.IAgentPlanHost",
        "Wino.Mail.ViewModels.Shell.Contracts.IShellMenuProvider",
        "Wino.Mail.ViewModels.Shell.Contracts.IMailShellClient",
        "Wino.Mail.ViewModels.Shell.Contracts.ICalendarShellClient",
        "Wino.Mail.ViewModels.Shell.Contracts.IShellViewModel",
        "Wino.Mail.ViewModels.Shell.Contracts.IShellHost",
        "Wino.Mail.ViewModels.Shell.Contracts.IShellMenuOwner",
        "Wino.Mail.ViewModels.Shell.Contracts.IShellMenuProviderResolver",
        "Wino.Mail.ViewModels.Shell.Contracts.IShellMenuSink");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(UiContractInBackend);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            if (start.Compilation.AssemblyName is not { } name || !BackendAssemblies.Contains(name)) return;

            start.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
            start.RegisterSymbolAction(AnalyzeField, SymbolKind.Field);
            start.RegisterSymbolAction(AnalyzeProperty, SymbolKind.Property);
            start.RegisterSymbolAction(AnalyzeEvent, SymbolKind.Event);
            start.RegisterSymbolAction(AnalyzeMethod, SymbolKind.Method);

            start.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
            start.RegisterOperationAction(AnalyzeMemberReference, OperationKind.PropertyReference, OperationKind.EventReference, OperationKind.MethodReference, OperationKind.FieldReference);
            start.RegisterOperationAction(AnalyzeObjectCreation, OperationKind.ObjectCreation);
            start.RegisterOperationAction(AnalyzeConversion, OperationKind.Conversion);
            start.RegisterOperationAction(AnalyzeTypeOf, OperationKind.TypeOf);
            start.RegisterOperationAction(AnalyzeIsType, OperationKind.IsType);
            start.RegisterOperationAction(AnalyzePattern, OperationKind.DeclarationPattern, OperationKind.TypePattern);
            start.RegisterOperationAction(AnalyzeVariableDeclarator, OperationKind.VariableDeclarator);
            start.RegisterOperationAction(AnalyzeDeclarationExpression, OperationKind.DeclarationExpression);
            start.RegisterOperationAction(AnalyzeForEach, OperationKind.Loop);
            start.RegisterOperationAction(AnalyzeAnonymousFunction, OperationKind.AnonymousFunction);
            start.RegisterOperationAction(AnalyzeLocalFunction, OperationKind.LocalFunction);
        });
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (!IsImplementation(type)) return;

        if (type.BaseType is { } baseType) ReportSymbol(context, type, baseType);

        foreach (var implemented in type.Interfaces)
        {
            ReportSymbol(context, type, implemented);
        }

        foreach (var parameter in type.TypeParameters)
        {
            foreach (var constraint in parameter.ConstraintTypes)
            {
                ReportSymbol(context, type, constraint);
            }
        }
    }

    private static void AnalyzeField(SymbolAnalysisContext context)
    {
        var field = (IFieldSymbol)context.Symbol;
        if (field.IsImplicitlyDeclared || !IsImplementation(field.ContainingType)) return;

        ReportSymbol(context, field, field.Type);
    }

    private static void AnalyzeProperty(SymbolAnalysisContext context)
    {
        var property = (IPropertySymbol)context.Symbol;
        if (!IsImplementation(property.ContainingType)) return;

        ReportSymbol(context, property, property.Type);

        foreach (var parameter in property.Parameters)
        {
            ReportSymbol(context, property, parameter.Type);
        }
    }

    private static void AnalyzeEvent(SymbolAnalysisContext context)
    {
        var @event = (IEventSymbol)context.Symbol;
        if (!IsImplementation(@event.ContainingType)) return;

        ReportSymbol(context, @event, @event.Type);
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (!IsImplementation(method.ContainingType)) return;
        if (method.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise) return;

        ReportSignature(context.ReportDiagnostic, context.Symbol, method, method.Locations);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;

        foreach (var argument in method.TypeArguments)
        {
            ReportOperation(context, argument);
        }

        ReportOperation(context, method.ContainingType);
    }

    private static void AnalyzeMemberReference(OperationAnalysisContext context)
    {
        var reference = (IMemberReferenceOperation)context.Operation;
        if (reference.Parent is IInvocationOperation) return;

        ReportOperation(context, reference.Member.ContainingType);
    }

    private static void AnalyzeObjectCreation(OperationAnalysisContext context)
        => ReportOperation(context, ((IObjectCreationOperation)context.Operation).Type);

    private static void AnalyzeConversion(OperationAnalysisContext context)
    {
        var conversion = (IConversionOperation)context.Operation;
        if (conversion.IsImplicit) return;

        ReportOperation(context, conversion.Type);
    }

    private static void AnalyzeTypeOf(OperationAnalysisContext context)
        => ReportOperation(context, ((ITypeOfOperation)context.Operation).TypeOperand);

    private static void AnalyzeIsType(OperationAnalysisContext context)
        => ReportOperation(context, ((IIsTypeOperation)context.Operation).TypeOperand);

    private static void AnalyzePattern(OperationAnalysisContext context)
    {
        switch (context.Operation)
        {
            case IDeclarationPatternOperation declaration:
                ReportOperation(context, declaration.MatchedType);
                break;
            case ITypePatternOperation typePattern:
                ReportOperation(context, typePattern.MatchedType);
                break;
        }
    }

    private static void AnalyzeVariableDeclarator(OperationAnalysisContext context)
        => ReportOperation(context, ((IVariableDeclaratorOperation)context.Operation).Symbol.Type);

    private static void AnalyzeDeclarationExpression(OperationAnalysisContext context)
        => ReportOperation(context, ((IDeclarationExpressionOperation)context.Operation).Type);

    private static void AnalyzeForEach(OperationAnalysisContext context)
    {
        if (context.Operation is not IForEachLoopOperation loop) return;

        foreach (var local in loop.Locals)
        {
            ReportOperation(context, local.Type);
        }
    }

    private static void AnalyzeAnonymousFunction(OperationAnalysisContext context)
    {
        var function = ((IAnonymousFunctionOperation)context.Operation).Symbol;
        var location = ImmutableArray.Create(context.Operation.Syntax.GetLocation());
        ReportSignature(context.ReportDiagnostic, context.ContainingSymbol, function, location);
    }

    private static void AnalyzeLocalFunction(OperationAnalysisContext context)
    {
        var function = ((ILocalFunctionOperation)context.Operation).Symbol;
        var location = function.Locations.IsEmpty ? ImmutableArray.Create(context.Operation.Syntax.GetLocation()) : function.Locations;
        ReportSignature(context.ReportDiagnostic, context.ContainingSymbol, function, location);
    }

    private static void ReportSignature(Action<Diagnostic> report, ISymbol site, IMethodSymbol method, ImmutableArray<Location> locations)
    {
        var location = locations.IsEmpty ? Location.None : locations[0];
        var name = method.MethodKind == MethodKind.AnonymousFunction ? site.ToDisplayString() : method.ToDisplayString();

        if (!method.ReturnsVoid) Report(report, site, location, name, method.ReturnType);

        foreach (var parameter in method.Parameters)
        {
            Report(report, site, location, $"{name} ({parameter.Name})", parameter.Type);
        }

        foreach (var typeParameter in method.TypeParameters)
        {
            foreach (var constraint in typeParameter.ConstraintTypes)
            {
                Report(report, site, location, name, constraint);
            }
        }
    }

    private static bool IsImplementation(INamedTypeSymbol? type)
        => type is { TypeKind: TypeKind.Class or TypeKind.Struct };

    private static void ReportSymbol(SymbolAnalysisContext context, ISymbol symbol, ITypeSymbol type)
    {
        var location = symbol.Locations.Length > 0 ? symbol.Locations[0] : Location.None;
        Report(context.ReportDiagnostic, symbol, location, symbol.ToDisplayString(), type);
    }

    private static void ReportOperation(OperationAnalysisContext context, ITypeSymbol? type)
    {
        if (!IsImplementation(context.ContainingSymbol as INamedTypeSymbol ?? context.ContainingSymbol.ContainingType)) return;

        Report(context.ReportDiagnostic, context.ContainingSymbol, context.Operation.Syntax.GetLocation(), context.ContainingSymbol.ToDisplayString(), type);
    }

    private static void Report(Action<Diagnostic> report, ISymbol site, Location location, string name, ITypeSymbol? type)
    {
        if (type is null || FindUiContract(type) is not { } contract) return;

        if (AnalyzerAllowList.AllowsUiContract(site, AnalyzerAllowList.FullName(contract))) return;

        report(Diagnostic.Create(UiContractInBackend, location, name, contract.Name));
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

                return named.ContainingType is { } outer ? FindUiContract(outer) : null;
            default:
                return null;
        }
    }

    private static bool IsUiContract(INamedTypeSymbol type)
        => type.TypeKind == TypeKind.Interface
           && UiContracts.Contains(AnalyzerAllowList.FullName(type));
}
