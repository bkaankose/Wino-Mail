using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Wino.SourceGenerators.Translator;

[Generator]
public class TranslatorSourceGenerator : IIncrementalGenerator
{
    private const string TranslatorAttributeName = "Wino.Core.SourceGeneration.Translator.TranslatorGenAttribute";

    private const string AttributeText =
"""
using System;
namespace Wino.Core.SourceGeneration.Translator
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public class TranslatorGenAttribute : Attribute
    {
    }
}
""";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Register the attribute source
        context.RegisterPostInitializationOutput(ctx => ctx.AddSource(
            "TranslatorGenAttribute.g.cs",
            SourceText.From(AttributeText, Encoding.UTF8)));

        // Get all classes with the TranslatorGenAttribute
        var classDeclarations = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                TranslatorAttributeName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (context, _) => (ClassDeclarationSyntax)context.TargetNode);

        // Get the JSON schema and track changes
        var jsonSchema = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith("en_US\\resources.json"))
            .Select((text, _) => (text, text.GetText()))
            .Collect()
            .WithTrackingName("JsonSchema");

        // Combine the JSON schema with the marked classes
        var combined = classDeclarations.Combine(jsonSchema);

        // Generate the source only when the JSON schema changes
        context.RegisterSourceOutput(combined,
            static (spc, source) => Execute(source.Left, source.Right, spc));
    }

    private static void Execute(
        ClassDeclarationSyntax classDeclaration,
        ImmutableArray<(AdditionalText, SourceText?)> jsonFiles,
        SourceProductionContext context)
    {
        var (_, jsonContent) = jsonFiles.FirstOrDefault();
        if (jsonContent == null) return;

        // Parse JSON
        var jsonString = jsonContent.ToString();
        var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonString);
        if (translations == null) return;

        // Generate the class
        var namespaceName = GetNamespaceName(classDeclaration);
        var className = classDeclaration.Identifier.Text;

        var sb = new StringBuilder();
        sb.AppendLine($$"""
            namespace {{namespaceName}}
            {
                public partial class {{className}}
                {
                    private static global::Wino.Core.Domain.Translations.WinoTranslationDictionary _dictionary;

            		public static global::Wino.Core.Domain.Translations.WinoTranslationDictionary Resources
            		{
            			get
            			{
            				if (_dictionary == null)
            				{
            					_dictionary = new global::Wino.Core.Domain.Translations.WinoTranslationDictionary();
            				}

            				return _dictionary;
            			}
            		}
            """);

        string[] escapeChars = [" ", ";", "@", "$", "&", "(", ")", "-", "#", ":", "!", "'", "?", "{", "}", ","];

        foreach (var translation in translations)
        {
            // Generate proper allowed variable name by C#
            var allowedPropertyName = escapeChars.Aggregate(translation.Key, (c1, c2) => c1.Replace(c2, string.Empty));

            // There might be null values for some keys. Those will display as (null string) in the Comment;
            // The actual translation for the key will be the key itself at runtime.
            var beautifiedValue = translation.Value ?? "(null string)";

            // We need to trim the line ending literals for comments.
            var beautifiedComment = beautifiedValue.Replace('\r', ' ').Replace('\n', ' ');
            AddKey(sb, allowedPropertyName, beautifiedComment);
        }

        sb.AppendLine($"{Spacing(1)}}}");
        sb.AppendLine("}");

        context.AddSource($"{className}.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static void AddKey(StringBuilder sb, string key, string? comment = null, int tabPos = 2)
    {
        var tabString = Spacing(tabPos);
        _ = sb.AppendLine();
        _ = sb.AppendLine($"{tabString}/// <summary>");
        _ = sb.AppendLine($"{tabString}/// {comment}");
        _ = sb.AppendLine($"{tabString}/// </summary>");
        _ = sb.AppendLine($"{tabString}public static string {key} => Resources.GetTranslatedString(\"{key}\");");
    }

    /// <summary>
    /// intent
    /// </summary>
    /// <param name="n">tab</param>
    /// <returns>4n*space</returns>
    internal static string Spacing(int n)
    {
        Span<char> spaces = stackalloc char[n * 4];
        spaces.Fill(' ');

        var sb = new StringBuilder(n * 4);
        foreach (var c in spaces)
            _ = sb.Append(c);

        return sb.ToString();
    }

    private static string GetNamespaceName(ClassDeclarationSyntax classDeclaration)
    {
        var namespaceName = string.Empty;
        var potentialNamespaceParent = classDeclaration.Parent;

        while (potentialNamespaceParent != null &&
               potentialNamespaceParent is not NamespaceDeclarationSyntax &&
               potentialNamespaceParent is not FileScopedNamespaceDeclarationSyntax)
        {
            potentialNamespaceParent = potentialNamespaceParent.Parent;
        }

        if (potentialNamespaceParent is BaseNamespaceDeclarationSyntax namespaceParent)
        {
            namespaceName = namespaceParent.Name.ToString();
        }

        return namespaceName;
    }
}
