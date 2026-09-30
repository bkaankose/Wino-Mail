using FluentAssertions;
using Wino.Core.Domain.Extensions;
using Xunit;

namespace Wino.Core.Tests.Models;

public sealed class CalendarRemoteEventIdTests
{
    [Theory]
    [InlineData("uid", "uid")]
    [InlineData("uid::20260914T100000Z", "uid::20260914T100000Z")]
    [InlineData("uid::20260914T100000", "uid::20260914T100000")]
    [InlineData("uid::20260914", "uid::20260914")]
    [InlineData("urn::event::20260914T100000Z", "urn::event::20260914T100000Z")]
    [InlineData("uid::4f4dd312f2334ad8a8ff789335932b59", "uid")]
    [InlineData("uid::4f4dd312-f233-4ad8-a8ff-789335932b59", "uid")]
    [InlineData("uid::20260914T100000Z::4f4dd312f2334ad8a8ff789335932b59", "uid::20260914T100000Z")]
    public void GetProviderRemoteEventId_RemovesOnlyGuidTrackingSuffixes(string storedId, string providerId)
    {
        storedId.GetProviderRemoteEventId().Should().Be(providerId);
    }
}
