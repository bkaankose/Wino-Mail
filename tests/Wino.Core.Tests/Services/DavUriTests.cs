using FluentAssertions;
using Wino.Services.Dav;
using Xunit;

namespace Wino.Core.Tests.Services;

public class DavUriTests
{
    private static readonly Uri Base = new("https://p42-caldav.icloud.com/123456/principal/");

    [Fact]
    public void RootedHref_ResolvesAgainstServer()
        => DavUri.Resolve(Base, "/123456/calendars/").AbsoluteUri.Should().Be("https://p42-caldav.icloud.com/123456/calendars/");

    [Fact]
    public void RelativeHref_ResolvesAgainstRequest()
        => DavUri.Resolve(Base, "home/").AbsoluteUri.Should().Be("https://p42-caldav.icloud.com/123456/principal/home/");

    [Fact]
    public void AbsoluteHttpHref_IsKept()
        => DavUri.Resolve(Base, "https://p01-caldav.icloud.com/9/calendars/").AbsoluteUri.Should().Be("https://p01-caldav.icloud.com/9/calendars/");

    [Fact]
    public void RootedLocation_ResolvesAgainstServer()
    {
        // HttpClient parses a rooted Location header as RelativeOrAbsolute, which is file:// on Unix.
        var location = new Uri("/123456/calendars/home/", UriKind.RelativeOrAbsolute);

        DavUri.Resolve(Base, location).AbsoluteUri.Should().Be("https://p42-caldav.icloud.com/123456/calendars/home/");
    }
}
