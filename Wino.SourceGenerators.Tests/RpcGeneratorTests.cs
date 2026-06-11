using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Wino.SourceGenerators.Rpc;
using Xunit;
using System;
using System.IO;
using System.Linq;

namespace Wino.SourceGenerators.Tests;

public class RpcGeneratorTests
{
    private const string CommonSource =
        """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Wino.Core.Domain.Attributes
        {
            [AttributeUsage(AttributeTargets.Interface)]
            public sealed class WinoRpcServiceAttribute : Attribute;

            [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Event)]
            public sealed class WinoRpcExcludeAttribute : Attribute;
        }

        namespace Wino.Core.Domain.Interfaces
        {
            public interface IUIMessage;
        }

        namespace Wino.Core.Domain.Entities
        {
            public class MailEntity
            {
                public Guid Id { get; set; }
                public string Subject { get; set; }
            }
        }
        """;

    private static (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<(string HintName, string Source)> Generated) RunGenerators(string source)
    {
        var syntaxTrees = new[]
        {
            CSharpSyntaxTree.ParseText(CommonSource),
            CSharpSyntaxTree.ParseText(source),
        };

        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName: "Wino.Ipc.Contracts",
            syntaxTrees: syntaxTrees,
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var runResult = CSharpGeneratorDriver.Create(new RpcGenerator(), new RpcEventRegistryGenerator())
            .RunGenerators(compilation)
            .GetRunResult();

        var generated = runResult.Results
            .SelectMany(r => r.GeneratedSources)
            .Select(s => (s.HintName, s.SourceText.ToString()))
            .ToList();

        return (runResult.Diagnostics.ToList(), generated);
    }

    [Fact]
    public void Generates_Proxy_Dispatcher_And_Records_For_Async_Methods()
    {
        var (diagnostics, generated) = RunGenerators(
            """
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Wino.Core.Domain.Entities;

            namespace Wino.Core.Domain.Interfaces
            {
                [Wino.Core.Domain.Attributes.WinoRpcService]
                public interface ITestMailService
                {
                    Task<List<MailEntity>> GetMailsAsync(Guid folderId, CancellationToken cancellationToken = default);
                    Task DeleteMailAsync(Guid mailId);
                    Task<(MailEntity mail, string mime)> CreateDraftAsync(Guid accountId);
                }
            }
            """);

        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();

        var rpcSource = generated.Single(g => g.HintName == "ITestMailService.Rpc.g.cs").Source;

        // Request records.
        rpcSource.Should().Contain("public sealed record ITestMailService_GetMailsAsync_0Request(global::System.Guid folderId);");
        rpcSource.Should().Contain("public sealed record ITestMailService_DeleteMailAsync_0Request(global::System.Guid mailId);");

        // Tuple return wrapped in a response record with named properties.
        rpcSource.Should().Contain("public sealed record ITestMailService_CreateDraftAsync_0Response(global::Wino.Core.Domain.Entities.MailEntity Mail, global::System.String Mime);");

        // Proxy passes the cancellation token and uses the stable method id.
        rpcSource.Should().Contain("\"ITestMailService.GetMailsAsync#0\"");
        rpcSource.Should().Contain("class TestMailServiceRemoteProxy : global::Wino.Core.Domain.Interfaces.ITestMailService");

        // Writes carry an operation id; reads do not.
        rpcSource.Should().Contain("\"ITestMailService.DeleteMailAsync#0\", new ITestMailService_DeleteMailAsync_0Request(mailId), global::Wino.Ipc.Contracts.WinoIpcJson.GetTypeInfo<ITestMailService_DeleteMailAsync_0Request>(), global::System.Guid.NewGuid()");

        // Dispatcher unwraps onto the real service.
        rpcSource.Should().Contain("class TestMailServiceDispatcher");
        rpcSource.Should().Contain("await _testMailService.GetMailsAsync(request.folderId, cancellationToken).ConfigureAwait(false)");

        // Composed dispatcher routes the interface.
        var composed = generated.Single(g => g.HintName == "WinoRpcDispatcher.g.cs").Source;
        composed.Should().Contain("_dispatchers[\"ITestMailService\"] = new TestMailServiceDispatcher(testMailService);");
    }

    [Fact]
    public void NonSerializable_Parameter_Produces_Error_Diagnostic()
    {
        var (diagnostics, generated) = RunGenerators(
            """
            using System.Threading.Tasks;

            namespace Wino.Core.Domain.Interfaces
            {
                public interface INotSerializable { }

                [Wino.Core.Domain.Attributes.WinoRpcService]
                public interface IBrokenService
                {
                    Task DoWorkAsync(INotSerializable input);
                }
            }
            """);

        diagnostics.Should().Contain(d => d.Id == "WINORPC001" && d.Severity == DiagnosticSeverity.Error);
        generated.Should().NotContain(g => g.HintName == "IBrokenService.Rpc.g.cs");
    }

    [Fact]
    public void Excluded_Member_Throws_In_Proxy_Instead_Of_Failing()
    {
        var (diagnostics, generated) = RunGenerators(
            """
            using System.Threading.Tasks;

            namespace Wino.Core.Domain.Interfaces
            {
                public interface INotSerializable { }

                [Wino.Core.Domain.Attributes.WinoRpcService]
                public interface IPartialService
                {
                    Task<int> CountAsync();

                    [Wino.Core.Domain.Attributes.WinoRpcExclude]
                    Task DoLocalWorkAsync(INotSerializable input);

                    [Wino.Core.Domain.Attributes.WinoRpcExclude]
                    INotSerializable LocalProperty { get; }
                }
            }
            """);

        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();

        var rpcSource = generated.Single(g => g.HintName == "IPartialService.Rpc.g.cs").Source;
        rpcSource.Should().Contain("DoLocalWorkAsync");
        rpcSource.Should().Contain("NotSupportedException");
        rpcSource.Should().Contain("LocalProperty");

        // Excluded members never get request records or dispatcher cases.
        rpcSource.Should().NotContain("DoLocalWorkAsync_0Request");
        rpcSource.Should().NotContain("IPartialService.DoLocalWorkAsync#");
    }

    [Fact]
    public void NonExcluded_Property_Produces_Error_Diagnostic()
    {
        var (diagnostics, _) = RunGenerators(
            """
            using System.Threading.Tasks;

            namespace Wino.Core.Domain.Interfaces
            {
                [Wino.Core.Domain.Attributes.WinoRpcService]
                public interface IPropertyService
                {
                    string Name { get; set; }
                }
            }
            """);

        diagnostics.Should().Contain(d => d.Id == "WINORPC002" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void EventRegistry_Covers_UIMessages_BothDirections()
    {
        var (diagnostics, generated) = RunGenerators(
            """
            using System;

            namespace Wino.Messaging.UI
            {
                public record TestMailAdded(Guid MailId) : Wino.Core.Domain.Interfaces.IUIMessage;
                public record TestMailRemoved(Guid MailId) : Wino.Core.Domain.Interfaces.IUIMessage;
            }
            """);

        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();

        var registry = generated.Single(g => g.HintName == "WinoRpcEventRegistry.g.cs").Source;

        registry.Should().Contain("case global::Wino.Messaging.UI.TestMailAdded typedMessage:");
        registry.Should().Contain("typeName = \"TestMailAdded\";");
        registry.Should().Contain("case \"TestMailRemoved\":");
        registry.Should().Contain("IMessengerExtensions.Send(messenger, message)");
    }
}
