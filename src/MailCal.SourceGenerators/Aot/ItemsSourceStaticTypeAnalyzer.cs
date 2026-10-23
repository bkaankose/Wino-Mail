using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace MailCal.SourceGenerators.Aot;

/// <summary>
/// Native AOT: a list control only accepts a managed collection whose WinRT vector support CsWinRT generated,
/// and CsWinRT generates it only for concrete types it sees cross into WinRT. A value that reaches an items
/// source through an interface or <see cref="object"/> static type hides its runtime type, so the control
/// rejects it in Release with "Argument 'source' is not a supported vector" while Debug works.
/// The check covers compiled bindings, whose generated setters live in the XAML compiler's output,
/// and items sources assigned in code.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ItemsSourceStaticTypeAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "MCAOT001";

    private static readonly DiagnosticDescriptor HiddenCollectionType = new(
        id: DiagnosticId,
        title: "Items source has no concrete collection type",
        messageFormat: "{0}{2} receives a value of static type '{1}'. Native AOT only generates WinRT vector support for concrete types, so the runtime collection can be rejected in Release. Expose a concrete type: an array, List<T>, ObservableCollection<T>, or a [GeneratedWinRTExposedType] collection.",
        category: "MailCalAot",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: "docs/harness/development-windows.md#native-aot-and-build-safety--critical");

    // XamlBindingSetters.Set_<Namespace>_<Type>_<Member>: ItemsSource, MenuItemsSource, FooterMenuItemsSource,
    // TabItemsSource, and CollectionViewSource.Source.
    private static readonly Regex SetterName = new(@"^Set_\w+_(\w*ItemsSource|CollectionViewSource_Source)$", RegexOptions.Compiled);

    // Values of x:Load or phased elements are parked in obj<N>ItemsSourceDeferredValue until the element exists.
    // The XAML compiler marks each generated setter with the markup it came from: "// Views\Page.xaml line 42".
    private static readonly Regex MarkupComment = new(@"//\s*(\S+\.xaml line \d+)", RegexOptions.Compiled);

    private static readonly Regex DeferredField = new(@"^obj\d+\w*ItemsSourceDeferredValue$", RegexOptions.Compiled);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(HiddenCollectionType);

    public override void Initialize(AnalysisContext context)
    {
        // The bindings that matter are in the XAML compiler's generated code.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();

        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        context.RegisterOperationAction(AnalyzeAssignment, OperationKind.SimpleAssignment);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;

        if (method.ContainingType?.Name != "XamlBindingSetters" || !SetterName.IsMatch(method.Name)) return;

        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter?.Name != "value") continue;

            // The deferred field is typed object; its real value is checked where it is assigned.
            if (Unwrap(argument.Value) is IFieldReferenceOperation field && DeferredField.IsMatch(field.Field.Name)) continue;

            var parts = method.Name.Split('_');
            Report(context, argument.Value, $"{parts[parts.Length - 2]}.{parts[parts.Length - 1]}");
        }
    }

    private static void AnalyzeAssignment(OperationAnalysisContext context)
    {
        var assignment = (ISimpleAssignmentOperation)context.Operation;

        string? target = assignment.Target switch
        {
            IFieldReferenceOperation field when DeferredField.IsMatch(field.Field.Name) => field.Field.Name,
            IPropertyReferenceOperation property when IsItemsSourceProperty(property.Property) => $"{property.Property.ContainingType.Name}.{property.Property.Name}",
            _ => null
        };

        if (target is not null) Report(context, assignment.Value, target);
    }

    private static bool IsItemsSourceProperty(IPropertySymbol property)
    {
        if (property.Type.SpecialType != SpecialType.System_Object) return false;

        return property.Name.EndsWith("ItemsSource", System.StringComparison.Ordinal)
            || (property.Name == "Source" && InheritsFrom(property.ContainingType, "CollectionViewSource"));
    }

    private static void Report(OperationAnalysisContext context, IOperation value, string target)
    {
        var operand = Unwrap(value);

        if (operand is IConditionalOperation conditional && conditional.WhenFalse is not null)
        {
            Report(context, conditional.WhenTrue, target);
            Report(context, conditional.WhenFalse, target);
            return;
        }

        if (IsPassThrough(context, operand)) return;

        if (operand.ConstantValue.HasValue && operand.ConstantValue.Value is null) return;

        // Reading an items source back, as the binding code does before deferring it, moves no new collection.
        if (operand is IPropertyReferenceOperation source && IsItemsSourceProperty(source.Property)) return;

        var type = operand.Type;
        if (type is null || !HidesRuntimeType(type)) return;

        context.ReportDiagnostic(Diagnostic.Create(HiddenCollectionType, value.Syntax.GetLocation(), target, type.ToDisplayString(), FindMarkup(value.Syntax)));
    }

    /// <summary>
    /// Values forwarded from another items source were already checked where they entered it: the generic setter
    /// plumbing the XAML compiler emits, and a control handing its own ItemsSource change to an inner list.
    /// </summary>
    private static bool IsPassThrough(OperationAnalysisContext context, IOperation operand)
    {
        if (operand is not IParameterReferenceOperation) return false;

        var method = context.ContainingSymbol as IMethodSymbol;
        if (method is null) return false;

        return method.ContainingType?.Name is "XamlBindingSetters" or "XamlTypeInfoProvider"
            || (method.Name.StartsWith("On", System.StringComparison.Ordinal) && method.Name.EndsWith("ItemsSourceChanged", System.StringComparison.Ordinal));
    }

    /// <summary>Names the XAML element behind generated binding code, so the error points at the markup to change.</summary>
    private static string FindMarkup(SyntaxNode syntax)
    {
        var text = syntax.SyntaxTree.GetText();
        var line = text.Lines.GetLineFromPosition(syntax.SpanStart).LineNumber;

        for (var index = line; index >= 0 && index > line - 8; index--)
        {
            var match = MarkupComment.Match(text.Lines[index].ToString());
            if (match.Success) return $" ({match.Groups[1].Value})";
        }

        return string.Empty;
    }

    private static IOperation Unwrap(IOperation operation)
    {
        // An explicit upcast such as (object)value hides the same type an implicit conversion would.
        while (operation is IConversionOperation conversion && (conversion.IsImplicit || conversion.Conversion.IsImplicit))
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    /// <summary>
    /// True when the static type cannot tell CsWinRT which concrete collection arrives at runtime.
    /// WinRT-projected interfaces such as ICollectionView wrap native objects and need no generated support.
    /// </summary>
    private static bool HidesRuntimeType(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_Object) return true;
        if (type.TypeKind == TypeKind.Dynamic || ContainsTypeParameter(type)) return true;
        if (type.TypeKind != TypeKind.Interface) return false;

        return !IsWinRTProjected(type);
    }

    /// <summary>A generic helper's T[] or List&lt;T&gt; is only made concrete at its call sites, which CsWinRT never sees.</summary>
    private static bool ContainsTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol array => ContainsTypeParameter(array.ElementType),
        INamedTypeSymbol named => named.TypeArguments.Any(ContainsTypeParameter),
        _ => false
    };

    private static bool IsWinRTProjected(ITypeSymbol type)
    {
        foreach (var attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass?.Name == "WindowsRuntimeTypeAttribute") return true;
        }

        var ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        return ns.StartsWith("Microsoft.UI.", System.StringComparison.Ordinal)
            || ns.StartsWith("Windows.", System.StringComparison.Ordinal);
    }

    private static bool InheritsFrom(INamedTypeSymbol? type, string name)
    {
        for (; type is not null; type = type.BaseType)
        {
            if (type.Name == name) return true;
        }

        return false;
    }
}
