using FluentAssertions;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Models.Calendar;
using Xunit;

namespace Wino.Core.Tests.Models;

public sealed class CalendarJoinLinkResolverTests
{
    public static TheoryData<string, string> ProviderUrls => new()
    {
        { VideoConferenceService.ServiceIdentifier.GoogleHangouts, "https://hangouts.google.com/call/abc" },
        { VideoConferenceService.ServiceIdentifier.GoogleMeet, "https://meet.google.com/abc-defg-hij" },
        { VideoConferenceService.ServiceIdentifier.Zoom, "https://acme.zoom.us/j/123456789?pwd=abc" },
        { VideoConferenceService.ServiceIdentifier.ZoomGovernment, "https://agency.zoomgov.us/j/123456789" },
        { VideoConferenceService.ServiceIdentifier.ZoomNative, "zoommtg://zoom.us/join?confno=123456789" },
        { VideoConferenceService.ServiceIdentifier.Webex, "https://acme.webex.com/meet/person" },
        { VideoConferenceService.ServiceIdentifier.WebexGovernment, "https://agency.webexgov.com/meet/person" },
        { VideoConferenceService.ServiceIdentifier.WebexWebinar, "https://acme.webex.com/acme/j.php?MTID=abc" },
        { VideoConferenceService.ServiceIdentifier.MicrosoftTeams, "https://teams.microsoft.com/l/meetup-join/19%3ameeting_abc%40thread.v2/0?context=abc" },
        { VideoConferenceService.ServiceIdentifier.MicrosoftTeamsGovernment, "https://teams.microsoft.us/l/meetup-join/19%3ameeting_abc%40thread.v2/0" },
        { VideoConferenceService.ServiceIdentifier.GoTo, "https://global.gotomeeting.com/join/123456789" },
        { VideoConferenceService.ServiceIdentifier.GoToWebinar, "https://attendee.gotowebinar.com/register/123456789" },
        { VideoConferenceService.ServiceIdentifier.Bluejeans, "https://bluejeans.com/123456789" },
        { VideoConferenceService.ServiceIdentifier.JoinMe, "https://join.me/example-room" },
        { VideoConferenceService.ServiceIdentifier.Whereby, "https://whereby.com/example-room" },
        { VideoConferenceService.ServiceIdentifier.RingCentral, "https://v.ringcentral.com/join/123456789" },
        { VideoConferenceService.ServiceIdentifier.AmazonChime, "https://chime.aws/123456789" },
        { VideoConferenceService.ServiceIdentifier.FaceTime, "https://facetime.apple.com/join#v=1&p=abc" },
        { VideoConferenceService.ServiceIdentifier.Jitsi, "https://meet.jit.si/example-room" },
        { VideoConferenceService.ServiceIdentifier.YouCanBookMe, "https://example.youcanbook.me/google-meet/" },
        { VideoConferenceService.ServiceIdentifier.Entre, "https://joinentre.com/room/example" },
        { VideoConferenceService.ServiceIdentifier.Riverside, "https://riverside.fm/studio/example" },
        { VideoConferenceService.ServiceIdentifier.SlackHuddle, "https://app.slack.com/huddle/T123/C456" },
        { VideoConferenceService.ServiceIdentifier.FacebookWorkspace, "https://acme.workplace.com/video/example" },
        { VideoConferenceService.ServiceIdentifier.Around, "https://around.co/r/example" },
        { VideoConferenceService.ServiceIdentifier.TeamSpeak, "ts3server://voice.example.com" },
        { VideoConferenceService.ServiceIdentifier.DoxyMe, "https://doxy.me/example" },
        { VideoConferenceService.ServiceIdentifier.Signal, "https://signal.link/call/#key=abc" },
        { VideoConferenceService.ServiceIdentifier.WhatsApp, "https://call.whatsapp.com/abc/room" },
        { VideoConferenceService.ServiceIdentifier.Tuple, "https://tuple.app/c/example" },
        { VideoConferenceService.ServiceIdentifier.Roam, "https://ro.am/r/#/d/example" }
    };

    [Theory]
    [MemberData(nameof(ProviderUrls))]
    public void TryGetService_MatchesPortedProvider(string expectedServiceId, string url)
    {
        VideoConferenceService.TryGetService(new Uri(url), out var service).Should().BeTrue();
        service.Id.Should().Be(expectedServiceId);
    }

    [Fact]
    public void FindVideoConferenceLink_UsesFirstRecognizedLinkInDocumentOrder()
    {
        const string html = """
            <p>Documentation: https://example.com/help</p>
            <a href="https://meet.google.com/abc-defg-hij?authuser=1&amp;hs=122">Join Google Meet</a>
            <p>Backup: https://whereby.com/backup-room</p>
            """;

        var result = CalendarJoinLinkResolver.FindVideoConferenceLink(html);

        result.Should().NotBeNull();
        result.Service.Should().Be(VideoConferenceService.GoogleMeet);
        result.Url.AbsoluteUri.Should().Be("https://meet.google.com/abc-defg-hij?authuser=1&hs=122");
    }

    [Fact]
    public void FindVideoConferenceLink_NormalizesSchemeLessLinkAndTrimsPunctuation()
    {
        var result = CalendarJoinLinkResolver.FindVideoConferenceLink("Join at meet.google.com/abc-defg-hij.");

        result.Should().NotBeNull();
        result.Url.AbsoluteUri.Should().Be("https://meet.google.com/abc-defg-hij");
    }

    [Fact]
    public void FindVideoConferenceLink_UnwrapsOutlookSafeLink()
    {
        const string meetingUrl = "https://teams.microsoft.com/l/meetup-join/19%3ameeting_abc%40thread.v2/0?context=abc";
        var safeLink = $"https://eur02.safelinks.protection.outlook.com/?url={Uri.EscapeDataString(meetingUrl)}&data=abc";

        var result = CalendarJoinLinkResolver.FindVideoConferenceLink($"<a href=\"{safeLink}\">Join</a>");

        result.Should().NotBeNull();
        result.Url.AbsoluteUri.Should().Be(meetingUrl);
    }

    [Fact]
    public void FindVideoConferenceLink_RejectsUnrelatedAndMalformedLinks()
    {
        CalendarJoinLinkResolver.FindVideoConferenceLink("https://example.com/meeting javascript:alert(1)")
            .Should().BeNull();
    }

    [Fact]
    public void ResolveDirectJoinLink_PrefersStructuredUrl()
    {
        var result = CalendarJoinLinkResolver.ResolveDirectJoinLink(
            "https://provider.example/join/structured",
            "https://meet.google.com/abc-defg-hij");

        result.Should().Be("https://provider.example/join/structured");
    }

    [Fact]
    public void TryGetEffectiveJoinUri_PrefersDirectLinkAndFallsBackToHtmlLink()
    {
        var item = new CalendarItem
        {
            DirectJoinLink = "https://meet.google.com/abc-defg-hij",
            HtmlLink = "https://calendar.example/events/1"
        };

        CalendarJoinLinkResolver.TryGetEffectiveJoinUri(item, out var directUri).Should().BeTrue();
        directUri.AbsoluteUri.Should().Be(item.DirectJoinLink);

        item.DirectJoinLink = null;
        CalendarJoinLinkResolver.TryGetEffectiveJoinUri(item, out var fallbackUri).Should().BeTrue();
        fallbackUri.AbsoluteUri.Should().Be(item.HtmlLink);
    }
}
