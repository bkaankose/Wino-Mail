using FluentAssertions;
using Moq;
using Wino.Core.Domain.Interfaces;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class CloudIntelligenceBackendTests
{
    [Fact]
    public void CloudBackend_DoesNotUseLocalVectorStore()
    {
        var backend = new CloudIntelligenceBackend(Mock.Of<IWinoAccountApiClient>());

        backend.Kind.Should().Be(IntelligenceBackendKind.Cloud);
        backend.UsesLocalVectorStore.Should().BeFalse();
    }
}
