using FluentAssertions;
using Wino.Core.Domain.Interfaces;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class CloudIntelligenceBackendTests
{
    [Fact]
    public void CloudBackend_UsesDownloadedLocalVectorStore()
    {
        var backend = new CloudIntelligenceBackend();

        backend.Kind.Should().Be(IntelligenceBackendKind.Cloud);
        backend.UsesLocalVectorStore.Should().BeTrue();
    }
}
