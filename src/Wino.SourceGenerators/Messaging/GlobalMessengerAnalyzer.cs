using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Wino.SourceGenerators.Messaging;

/// <summary>
/// The shared backend takes its messenger from the container, so a host can give each instance its own. A direct
/// read of the process-wide <c>WeakReferenceMessenger.Default</c> or <c>StrongReferenceMessenger.Default</c> bypasses
/// that. The apps' single registration of the global messenger in <c>ServicesContainerSetup.cs</c> is the exception.
/// The heads are out of scope.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GlobalMessengerAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "WINOMSG001";

    private static readonly DiagnosticDescriptor GlobalMessenger = new(
        id: DiagnosticId,
        title: "Shared code uses the process-wide messenger",
        messageFormat: "'{0}.Default' is the process-wide messenger. Take CommunityToolkit.Mvvm.Messaging.IMessenger through the constructor (a ViewModel uses its Messenger property, a request its IUIMessageSender argument).",
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

    private const string RegistrationFileName = "ServicesContainerSetup.cs";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(GlobalMessenger);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            if (start.Compilation.AssemblyName is not { } name || !SharedAssemblies.Contains(name)) return;

            start.RegisterOperationAction(AnalyzePropertyReference, OperationKind.PropertyReference);
        });
    }

    private static void AnalyzePropertyReference(OperationAnalysisContext context)
    {
        var reference = (IPropertyReferenceOperation)context.Operation;
        var property = reference.Property;

        if (property.Name != "Default" || !property.IsStatic) return;

        var type = property.ContainingType;
        if (type is null || type.ContainingNamespace?.ToDisplayString() != "CommunityToolkit.Mvvm.Messaging") return;
        if (type.Name is not ("WeakReferenceMessenger" or "StrongReferenceMessenger")) return;

        if (IsSharedRegistration(reference)) return;

        context.ReportDiagnostic(Diagnostic.Create(GlobalMessenger, reference.Syntax.GetLocation(), type.Name));
    }

    /// <summary>The apps register the global messenger as the container's IMessenger once: services.AddSingleton&lt;IMessenger&gt;(…Default).</summary>
    private static bool IsSharedRegistration(IOperation reference)
    {
        var path = reference.Syntax.SyntaxTree.FilePath ?? string.Empty;
        if (!path.EndsWith(RegistrationFileName, StringComparison.OrdinalIgnoreCase)) return false;

        var parent = reference.Parent;
        while (parent is IConversionOperation) parent = parent.Parent;

        return parent is IArgumentOperation { Parent: IInvocationOperation invocation }
               && invocation.TargetMethod.Name == "AddSingleton"
               && invocation.TargetMethod.TypeArguments.Length == 1
               && invocation.TargetMethod.TypeArguments[0].Name == "IMessenger";
    }
}
