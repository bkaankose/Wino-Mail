using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Wino.SourceGenerators.Rpc;

/// <summary>
/// Generates, inside Wino.Ipc.Contracts, for every [WinoRpcService] interface found in the
/// referenced Wino assemblies:
///  - strongly typed request (and tuple response) records per remoted method,
///  - a UI side {Name}RemoteProxy implementing the interface over IRpcClient,
///  - a companion side {Name}Dispatcher unwrapping requests onto the real implementation,
///  - a composed WinoRpcDispatcher routing by method id.
/// Members whose signatures cannot cross the pipe produce compile errors unless excluded.
/// </summary>
[Generator]
public sealed class RpcGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor NonSerializableSignature = new(
        id: "WINORPC001",
        title: "RPC member has a non-serializable signature",
        messageFormat: "Member '{0}' of RPC interface '{1}' uses non-serializable type '{2}'. Refactor the signature to serializable data or mark the member with [WinoRpcExclude].",
        category: "WinoRpc",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedMember = new(
        id: "WINORPC002",
        title: "RPC interface member kind is not supported",
        messageFormat: "Member '{0}' of RPC interface '{1}' is a {2}, which cannot be remoted. Mark it with [WinoRpcExclude] to keep it process-local.",
        category: "WinoRpc",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedMethodShape = new(
        id: "WINORPC003",
        title: "RPC method shape is not supported",
        messageFormat: "Method '{0}' of RPC interface '{1}' {2}. Mark it with [WinoRpcExclude] or refactor it.",
        category: "WinoRpc",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly string[] ReadMethodPrefixes =
    [
        "Get", "Fetch", "Search", "Find", "Is", "Are", "Has", "Check", "Test", "Synchronize", "Download", "Ping"
    ];

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterSourceOutput(context.CompilationProvider, static (spc, compilation) => Execute(spc, compilation));
    }

    private static void Execute(SourceProductionContext context, Compilation compilation)
    {
        if (compilation.AssemblyName != RpcModel.ContractsAssemblyName)
            return;

        var serviceInterfaces = RpcModel.GetAllWinoTypes(compilation)
            .Where(t => t.TypeKind == TypeKind.Interface && RpcModel.HasAttribute(t, RpcModel.ServiceAttributeMetadataName))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        if (serviceInterfaces.Count == 0)
            return;

        var generatedInterfaces = new List<InterfaceModel>();

        foreach (var serviceInterface in serviceInterfaces)
        {
            var model = BuildInterfaceModel(context, serviceInterface);

            if (model != null)
            {
                generatedInterfaces.Add(model);
                context.AddSource($"{model.InterfaceName}.Rpc.g.cs", SourceText.From(GenerateInterfaceSource(model), Encoding.UTF8));
            }
        }

        context.AddSource("WinoRpcDispatcher.g.cs", SourceText.From(GenerateComposedDispatcher(generatedInterfaces), Encoding.UTF8));
    }

    private sealed class ParameterModel
    {
        public string Name { get; set; }
        public string TypeDisplay { get; set; }
        public bool IsCancellationToken { get; set; }
    }

    private sealed class TupleElementModel
    {
        public string PropertyName { get; set; }
        public string TypeDisplay { get; set; }
    }

    private sealed class MethodModel
    {
        public string Name { get; set; }
        public string Uid { get; set; }
        public string RecordBaseName { get; set; }
        public bool IsExcluded { get; set; }
        public bool IsAsync { get; set; }
        public bool HasReturnValue { get; set; }
        public string ReturnTypeDisplay { get; set; } // inner T (without Task<>)
        public string DeclaredReturnTypeDisplay { get; set; }
        public List<ParameterModel> Parameters { get; set; }
        public List<TupleElementModel>? TupleElements { get; set; }
        public bool IsWrite { get; set; }
    }

    private sealed class ExcludedPropertyModel
    {
        public string Name { get; set; }
        public string TypeDisplay { get; set; }
        public bool HasGetter { get; set; }
        public bool HasSetter { get; set; }
    }

    private sealed class ExcludedEventModel
    {
        public string Name { get; set; }
        public string TypeDisplay { get; set; }
    }

    private sealed class InterfaceModel
    {
        public string InterfaceName { get; set; }
        public string InterfaceDisplay { get; set; }
        public string ProxyName { get; set; }
        public string DispatcherName { get; set; }
        public string ServiceFieldName { get; set; }
        public List<MethodModel> Methods { get; set; }
        public List<ExcludedPropertyModel> ExcludedProperties { get; set; }
        public List<ExcludedEventModel> ExcludedEvents { get; set; }
    }

    private static InterfaceModel? BuildInterfaceModel(SourceProductionContext context, INamedTypeSymbol serviceInterface)
    {
        var methods = new List<MethodModel>();
        var excludedProperties = new List<ExcludedPropertyModel>();
        var excludedEvents = new List<ExcludedEventModel>();
        var overloadCounters = new Dictionary<string, int>();
        var hasErrors = false;

        foreach (var member in serviceInterface.GetMembers())
        {
            // Skip static members and default interface implementations.
            if (member.IsStatic || !member.IsAbstract)
                continue;

            var isExcluded = RpcModel.HasAttribute(member, RpcModel.ExcludeAttributeMetadataName);
            var location = member.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None;

            switch (member)
            {
                case IPropertySymbol property:
                    if (!isExcluded)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(UnsupportedMember, location, property.Name, serviceInterface.Name, "property"));
                        hasErrors = true;
                        continue;
                    }

                    excludedProperties.Add(new ExcludedPropertyModel
                    {
                        Name = property.Name,
                        TypeDisplay = property.Type.ToDisplayString(RpcModel.FullyQualified),
                        HasGetter = property.GetMethod != null,
                        HasSetter = property.SetMethod != null,
                    });
                    continue;

                case IEventSymbol eventSymbol:
                    if (!isExcluded)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(UnsupportedMember, location, eventSymbol.Name, serviceInterface.Name, "event"));
                        hasErrors = true;
                        continue;
                    }

                    excludedEvents.Add(new ExcludedEventModel
                    {
                        Name = eventSymbol.Name,
                        TypeDisplay = eventSymbol.Type.ToDisplayString(RpcModel.FullyQualified),
                    });
                    continue;

                case IMethodSymbol method when method.MethodKind == MethodKind.Ordinary:
                {
                    overloadCounters.TryGetValue(method.Name, out var overloadIndex);
                    overloadCounters[method.Name] = overloadIndex + 1;

                    var methodModel = BuildMethodModel(context, serviceInterface, method, overloadIndex, isExcluded, location, ref hasErrors);

                    if (methodModel != null)
                        methods.Add(methodModel);

                    continue;
                }
            }
        }

        if (hasErrors)
            return null;

        var nameWithoutPrefix = serviceInterface.Name.StartsWith("I") ? serviceInterface.Name.Substring(1) : serviceInterface.Name;

        return new InterfaceModel
        {
            InterfaceName = serviceInterface.Name,
            InterfaceDisplay = serviceInterface.ToDisplayString(RpcModel.FullyQualified),
            ProxyName = $"{nameWithoutPrefix}RemoteProxy",
            DispatcherName = $"{nameWithoutPrefix}Dispatcher",
            ServiceFieldName = $"_{char.ToLowerInvariant(nameWithoutPrefix[0])}{nameWithoutPrefix.Substring(1)}",
            Methods = methods,
            ExcludedProperties = excludedProperties,
            ExcludedEvents = excludedEvents,
        };
    }

    private static MethodModel? BuildMethodModel(SourceProductionContext context,
                                                 INamedTypeSymbol serviceInterface,
                                                 IMethodSymbol method,
                                                 int overloadIndex,
                                                 bool isExcluded,
                                                 Location location,
                                                 ref bool hasErrors)
    {
        var parameters = new List<ParameterModel>();

        foreach (var parameter in method.Parameters)
        {
            parameters.Add(new ParameterModel
            {
                Name = parameter.Name,
                TypeDisplay = parameter.Type.ToDisplayString(RpcModel.FullyQualified),
                IsCancellationToken = parameter.Type.ToDisplayString() == "System.Threading.CancellationToken",
            });
        }

        // Determine async shape and the value type carried back.
        var returnType = method.ReturnType;
        bool isAsync = false;
        bool hasReturnValue;
        ITypeSymbol? valueType = null;

        if (returnType is INamedTypeSymbol namedReturn &&
            namedReturn.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks" &&
            namedReturn.Name == "Task")
        {
            isAsync = true;

            if (namedReturn.IsGenericType)
            {
                hasReturnValue = true;
                valueType = namedReturn.TypeArguments[0];
            }
            else
            {
                hasReturnValue = false;
            }
        }
        else if (returnType.SpecialType == SpecialType.System_Void)
        {
            hasReturnValue = false;
        }
        else
        {
            hasReturnValue = true;
            valueType = returnType;
        }

        var declaredReturnDisplay = returnType.ToDisplayString(RpcModel.FullyQualified);

        if (isExcluded)
        {
            return new MethodModel
            {
                Name = method.Name,
                Uid = string.Empty,
                RecordBaseName = string.Empty,
                IsExcluded = true,
                IsAsync = isAsync,
                HasReturnValue = hasReturnValue,
                ReturnTypeDisplay = valueType?.ToDisplayString(RpcModel.FullyQualified) ?? "void",
                DeclaredReturnTypeDisplay = declaredReturnDisplay,
                Parameters = parameters,
                TupleElements = null,
                IsWrite = false,
            };
        }

        if (method.IsGenericMethod)
        {
            context.ReportDiagnostic(Diagnostic.Create(UnsupportedMethodShape, location, method.Name, serviceInterface.Name, "is generic"));
            hasErrors = true;
            return null;
        }

        foreach (var parameter in method.Parameters)
        {
            if (parameter.RefKind != RefKind.None)
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedMethodShape, location, method.Name, serviceInterface.Name, $"has ref/out/in parameter '{parameter.Name}'"));
                hasErrors = true;
                return null;
            }

            if (parameter.Type.ToDisplayString() == "System.Threading.CancellationToken")
                continue;

            if (!RpcSerializabilityChecker.IsSerializable(parameter.Type, isReturnPosition: false))
            {
                context.ReportDiagnostic(Diagnostic.Create(NonSerializableSignature, location, method.Name, serviceInterface.Name, parameter.Type.ToDisplayString()));
                hasErrors = true;
                return null;
            }
        }

        List<TupleElementModel>? tupleElements = null;

        if (valueType != null)
        {
            if (!RpcSerializabilityChecker.IsSerializable(valueType, isReturnPosition: true))
            {
                context.ReportDiagnostic(Diagnostic.Create(NonSerializableSignature, location, method.Name, serviceInterface.Name, valueType.ToDisplayString()));
                hasErrors = true;
                return null;
            }

            if (valueType is INamedTypeSymbol { IsTupleType: true } tupleType)
            {
                tupleElements = new List<TupleElementModel>();

                foreach (var element in tupleType.TupleElements)
                {
                    var propertyName = element.Name;
                    propertyName = char.ToUpperInvariant(propertyName[0]) + propertyName.Substring(1);

                    tupleElements.Add(new TupleElementModel
                    {
                        PropertyName = propertyName,
                        TypeDisplay = element.Type.ToDisplayString(RpcModel.FullyQualified),
                    });
                }
            }
        }

        var isWrite = !ReadMethodPrefixes.Any(prefix => method.Name.StartsWith(prefix, StringComparison.Ordinal));

        return new MethodModel
        {
            Name = method.Name,
            Uid = $"{serviceInterface.Name}.{method.Name}#{overloadIndex}",
            RecordBaseName = $"{serviceInterface.Name}_{method.Name}_{overloadIndex}",
            IsExcluded = false,
            IsAsync = isAsync,
            HasReturnValue = hasReturnValue,
            ReturnTypeDisplay = valueType?.ToDisplayString(RpcModel.FullyQualified) ?? "void",
            DeclaredReturnTypeDisplay = declaredReturnDisplay,
            Parameters = parameters,
            TupleElements = tupleElements,
            IsWrite = isWrite,
        };
    }

    private static string GenerateInterfaceSource(InterfaceModel model)
    {
        var builder = new StringBuilder();

        builder.AppendLine("// <auto-generated by Wino RpcGenerator />");
        builder.AppendLine("#nullable disable");
        builder.AppendLine($"namespace {RpcModel.GeneratedNamespace}");
        builder.AppendLine("{");

        AppendRecords(builder, model);
        AppendProxy(builder, model);
        AppendDispatcher(builder, model);

        builder.AppendLine("}");

        return builder.ToString();
    }

    private static void AppendRecords(StringBuilder builder, InterfaceModel model)
    {
        foreach (var method in model.Methods.Where(m => !m.IsExcluded))
        {
            var recordParameters = string.Join(", ", method.Parameters
                .Where(p => !p.IsCancellationToken)
                .Select(p => $"{p.TypeDisplay} {p.Name}"));

            builder.AppendLine(recordParameters.Length == 0
                ? $"    public sealed record {method.RecordBaseName}Request;"
                : $"    public sealed record {method.RecordBaseName}Request({recordParameters});");

            if (method.TupleElements != null)
            {
                var responseParameters = string.Join(", ", method.TupleElements.Select(e => $"{e.TypeDisplay} {e.PropertyName}"));
                builder.AppendLine($"    public sealed record {method.RecordBaseName}Response({responseParameters});");
            }
        }

        builder.AppendLine();
    }

    private static void AppendProxy(StringBuilder builder, InterfaceModel model)
    {
        builder.AppendLine($"    /// <summary>UI-side proxy for <see cref=\"{model.InterfaceDisplay}\"/> over the IPC pipe.</summary>");
        builder.AppendLine($"    public sealed class {model.ProxyName} : {model.InterfaceDisplay}");
        builder.AppendLine("    {");
        builder.AppendLine("        private readonly global::Wino.Ipc.IRpcClient _rpcClient;");
        builder.AppendLine();
        builder.AppendLine($"        public {model.ProxyName}(global::Wino.Ipc.IRpcClient rpcClient)");
        builder.AppendLine("        {");
        builder.AppendLine("            _rpcClient = rpcClient;");
        builder.AppendLine("        }");

        foreach (var method in model.Methods)
        {
            builder.AppendLine();

            var parameterList = string.Join(", ", method.Parameters.Select(p => $"{p.TypeDisplay} {p.Name}"));

            if (method.IsExcluded)
            {
                builder.AppendLine($"        public {method.DeclaredReturnTypeDisplay} {method.Name}({parameterList})");
                builder.AppendLine($"            => throw new global::System.NotSupportedException(\"{model.InterfaceName}.{method.Name} is process-local and cannot be called through the RPC proxy.\");");
                continue;
            }

            var cancellationArgument = method.Parameters.FirstOrDefault(p => p.IsCancellationToken)?.Name ?? "default";
            var operationIdArgument = method.IsWrite ? "global::System.Guid.NewGuid()" : "null";
            var requestArguments = string.Join(", ", method.Parameters.Where(p => !p.IsCancellationToken).Select(p => p.Name));
            var requestCreation = $"new {method.RecordBaseName}Request({requestArguments})";

            var responseTypeDisplay = method.TupleElements != null
                ? $"{method.RecordBaseName}Response"
                : method.ReturnTypeDisplay;

            string invocation;

            if (method.HasReturnValue)
            {
                invocation = $"_rpcClient.InvokeAsync(\"{method.Uid}\", {requestCreation}, " +
                             $"global::Wino.Ipc.Contracts.WinoIpcJson.GetTypeInfo<{method.RecordBaseName}Request>(), " +
                             $"global::Wino.Ipc.Contracts.WinoIpcJson.GetTypeInfo<{responseTypeDisplay}>(), " +
                             $"{operationIdArgument}, {cancellationArgument})";
            }
            else
            {
                invocation = $"_rpcClient.InvokeAsync(\"{method.Uid}\", {requestCreation}, " +
                             $"global::Wino.Ipc.Contracts.WinoIpcJson.GetTypeInfo<{method.RecordBaseName}Request>(), " +
                             $"{operationIdArgument}, {cancellationArgument})";
            }

            if (method.IsAsync)
            {
                builder.AppendLine($"        public async {method.DeclaredReturnTypeDisplay} {method.Name}({parameterList})");
                builder.AppendLine("        {");

                if (!method.HasReturnValue)
                {
                    builder.AppendLine($"            await {invocation}.ConfigureAwait(false);");
                }
                else if (method.TupleElements != null)
                {
                    builder.AppendLine($"            var response = await {invocation}.ConfigureAwait(false);");
                    var tupleConstruction = string.Join(", ", method.TupleElements.Select(e => $"response.{e.PropertyName}"));
                    builder.AppendLine($"            return ({tupleConstruction});");
                }
                else
                {
                    builder.AppendLine($"            return await {invocation}.ConfigureAwait(false);");
                }

                builder.AppendLine("        }");
            }
            else
            {
                // Synchronous interface member: block on the call. These are rare, small payloads.
                builder.AppendLine($"        public {method.DeclaredReturnTypeDisplay} {method.Name}({parameterList})");

                if (!method.HasReturnValue)
                {
                    builder.AppendLine($"            => {invocation}.GetAwaiter().GetResult();");
                }
                else if (method.TupleElements != null)
                {
                    builder.AppendLine("        {");
                    builder.AppendLine($"            var response = {invocation}.GetAwaiter().GetResult();");
                    var tupleConstruction = string.Join(", ", method.TupleElements.Select(e => $"response.{e.PropertyName}"));
                    builder.AppendLine($"            return ({tupleConstruction});");
                    builder.AppendLine("        }");
                }
                else
                {
                    builder.AppendLine($"            => {invocation}.GetAwaiter().GetResult();");
                }
            }
        }

        foreach (var property in model.ExcludedProperties)
        {
            builder.AppendLine();
            builder.Append($"        public {property.TypeDisplay} {property.Name} {{ ");

            if (property.HasGetter)
                builder.Append($"get => throw new global::System.NotSupportedException(\"{model.InterfaceName}.{property.Name} is process-local.\"); ");

            if (property.HasSetter)
                builder.Append($"set => throw new global::System.NotSupportedException(\"{model.InterfaceName}.{property.Name} is process-local.\"); ");

            builder.AppendLine("}");
        }

        foreach (var eventModel in model.ExcludedEvents)
        {
            builder.AppendLine();
            builder.AppendLine($"        public event {eventModel.TypeDisplay} {eventModel.Name}");
            builder.AppendLine("        {");
            builder.AppendLine($"            add => throw new global::System.NotSupportedException(\"{model.InterfaceName}.{eventModel.Name} is process-local.\");");
            builder.AppendLine($"            remove => throw new global::System.NotSupportedException(\"{model.InterfaceName}.{eventModel.Name} is process-local.\");");
            builder.AppendLine("        }");
        }

        builder.AppendLine("    }");
        builder.AppendLine();
    }

    private static void AppendDispatcher(StringBuilder builder, InterfaceModel model)
    {
        builder.AppendLine($"    /// <summary>Companion-side dispatcher for <see cref=\"{model.InterfaceDisplay}\"/>.</summary>");
        builder.AppendLine($"    public sealed class {model.DispatcherName} : global::Wino.Ipc.IRpcRequestHandler");
        builder.AppendLine("    {");
        builder.AppendLine($"        private readonly {model.InterfaceDisplay} {model.ServiceFieldName};");
        builder.AppendLine();
        builder.AppendLine($"        public {model.DispatcherName}({model.InterfaceDisplay} service)");
        builder.AppendLine("        {");
        builder.AppendLine($"            {model.ServiceFieldName} = service;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public async global::System.Threading.Tasks.Task<byte[]> HandleRequestAsync(string methodName, global::System.Text.Json.JsonElement payload, global::System.Threading.CancellationToken cancellationToken)");
        builder.AppendLine("        {");
        builder.AppendLine("            switch (methodName)");
        builder.AppendLine("            {");

        foreach (var method in model.Methods.Where(m => !m.IsExcluded))
        {
            builder.AppendLine($"                case \"{method.Uid}\":");
            builder.AppendLine("                {");

            var hasPayloadParameters = method.Parameters.Any(p => !p.IsCancellationToken);

            if (hasPayloadParameters)
                builder.AppendLine($"                    var request = global::Wino.Ipc.Contracts.WinoIpcJson.Deserialize<{method.RecordBaseName}Request>(payload);");

            var callArguments = string.Join(", ", method.Parameters.Select(p => p.IsCancellationToken ? "cancellationToken" : $"request.{p.Name}"));
            var serviceCall = $"{model.ServiceFieldName}.{method.Name}({callArguments})";

            if (method.IsAsync)
                serviceCall = $"await {serviceCall}.ConfigureAwait(false)";

            if (!method.HasReturnValue)
            {
                builder.AppendLine($"                    {serviceCall};");
                builder.AppendLine("                    return null;");
            }
            else if (method.TupleElements != null)
            {
                builder.AppendLine($"                    var result = {serviceCall};");
                var responseArguments = string.Join(", ", Enumerable.Range(1, method.TupleElements.Count).Select(i => $"result.Item{i}"));
                builder.AppendLine($"                    return global::Wino.Ipc.Contracts.WinoIpcJson.SerializeToUtf8Bytes(new {method.RecordBaseName}Response({responseArguments}));");
            }
            else
            {
                builder.AppendLine($"                    var result = {serviceCall};");
                builder.AppendLine("                    return global::Wino.Ipc.Contracts.WinoIpcJson.SerializeToUtf8Bytes(result);");
            }

            builder.AppendLine("                }");
        }

        builder.AppendLine("                default:");
        builder.AppendLine($"                    throw new global::System.InvalidOperationException($\"Unknown RPC method '{{methodName}}' for {model.InterfaceName}.\");");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }

    private static string GenerateComposedDispatcher(List<InterfaceModel> models)
    {
        var builder = new StringBuilder();

        builder.AppendLine("// <auto-generated by Wino RpcGenerator />");
        builder.AppendLine("#nullable disable");
        builder.AppendLine($"namespace {RpcModel.GeneratedNamespace}");
        builder.AppendLine("{");
        builder.AppendLine("    /// <summary>Routes RPC requests to the per-interface dispatchers by method id prefix.</summary>");
        builder.AppendLine("    public sealed class WinoRpcDispatcher : global::Wino.Ipc.IRpcRequestHandler");
        builder.AppendLine("    {");
        builder.AppendLine("        private readonly global::System.Collections.Generic.Dictionary<string, global::Wino.Ipc.IRpcRequestHandler> _dispatchers = new();");
        builder.AppendLine();

        var constructorParameters = string.Join(",\n                                 ",
            models.Select(m => $"{m.InterfaceDisplay} {ParameterName(m)}"));

        builder.AppendLine($"        public WinoRpcDispatcher({constructorParameters})");
        builder.AppendLine("        {");

        foreach (var model in models)
        {
            builder.AppendLine($"            _dispatchers[\"{model.InterfaceName}\"] = new {model.DispatcherName}({ParameterName(model)});");
        }

        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        public global::System.Threading.Tasks.Task<byte[]> HandleRequestAsync(string methodName, global::System.Text.Json.JsonElement payload, global::System.Threading.CancellationToken cancellationToken)");
        builder.AppendLine("        {");
        builder.AppendLine("            var separatorIndex = methodName.IndexOf('.');");
        builder.AppendLine("            var interfaceName = separatorIndex > 0 ? methodName.Substring(0, separatorIndex) : methodName;");
        builder.AppendLine();
        builder.AppendLine("            if (!_dispatchers.TryGetValue(interfaceName, out var dispatcher))");
        builder.AppendLine("                throw new global::System.InvalidOperationException($\"No dispatcher registered for RPC method '{methodName}'.\");");
        builder.AppendLine();
        builder.AppendLine("            return dispatcher.HandleRequestAsync(methodName, payload, cancellationToken);");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine("}");

        return builder.ToString();

        static string ParameterName(InterfaceModel model)
        {
            var nameWithoutPrefix = model.InterfaceName.StartsWith("I") ? model.InterfaceName.Substring(1) : model.InterfaceName;
            return char.ToLowerInvariant(nameWithoutPrefix[0]) + nameWithoutPrefix.Substring(1);
        }
    }
}
