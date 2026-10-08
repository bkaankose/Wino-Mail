using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace Wino.Core.Domain.Models.Calendar;

public sealed class VideoConferenceService
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private readonly Regex _regex;

    public VideoConferenceService(string id, string pattern, string name, bool supportsNativeApp)
    {
        Id = id;
        Pattern = pattern;
        Name = name;
        SupportsNativeApp = supportsNativeApp;
        _regex = new Regex(
            pattern,
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout);
    }

    public string Id { get; }
    public string Pattern { get; }
    public string Name { get; }
    public bool SupportsNativeApp { get; }

    public bool Matches(Uri uri)
    {
        if (uri == null)
            return false;

        var matchingUri = TryUnwrapOutlookSafeLink(uri, out var unwrappedUri) ? unwrappedUri : uri;

        try
        {
            return _regex.IsMatch(matchingUri.AbsoluteUri);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    public static bool TryGetService(Uri uri, out VideoConferenceService service)
    {
        service = All.FirstOrDefault(candidate => candidate.Matches(uri));
        return service != null;
    }

    public static bool TryUnwrapOutlookSafeLink(Uri uri, out Uri unwrappedUri)
    {
        unwrappedUri = null;

        if (uri == null || !uri.IsAbsoluteUri ||
            !uri.AbsoluteUri.Contains("safelinks.protection.outlook.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = uri.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            var encodedName = separatorIndex < 0 ? pair : pair[..separatorIndex];
            var name = WebUtility.UrlDecode(encodedName);

            if (!string.Equals(name, "url", StringComparison.OrdinalIgnoreCase))
                continue;

            var encodedValue = separatorIndex < 0 ? string.Empty : pair[(separatorIndex + 1)..];
            var value = WebUtility.UrlDecode(encodedValue);

            return Uri.TryCreate(value, UriKind.Absolute, out unwrappedUri);
        }

        return false;
    }

    public static class ServiceIdentifier
    {
        public const string GoogleHangouts = "googleHangouts";
        public const string GoogleMeet = "googleMeet";
        public const string Zoom = "zoom";
        public const string ZoomGovernment = "zoomGovernment";
        public const string ZoomNative = "zoomNative";
        public const string Webex = "webex";
        public const string WebexGovernment = "webexGovernment";
        public const string WebexWebinar = "webexWebinar";
        public const string MicrosoftTeams = "microsoftTeams";
        public const string MicrosoftTeamsGovernment = "microsoftTeamsGovernment";
        public const string GoTo = "goTo";
        public const string GoToWebinar = "goToWbinar";
        public const string Bluejeans = "bluejeans";
        public const string JoinMe = "joinMe";
        public const string Whereby = "whereby";
        public const string RingCentral = "ringCentral";
        public const string AmazonChime = "amazonChime";
        public const string FaceTime = "faceTime";
        public const string Jitsi = "jitsi";
        public const string YouCanBookMe = "youcanbookme";
        public const string Entre = "entre";
        public const string Riverside = "riverside";
        public const string SlackHuddle = "slackHuddle";
        public const string FacebookWorkspace = "facebookWorkspace";
        public const string Skype = "skype";
        public const string Around = "around";
        public const string TeamSpeak = "teamSpeak";
        public const string DoxyMe = "doxyMe";
        public const string Signal = "signal";
        public const string WhatsApp = "whatsapp";
        public const string Tuple = "tuple";
        public const string Roam = "roam";
    }

    public static VideoConferenceService GoogleHangouts { get; } = new(ServiceIdentifier.GoogleHangouts, @"(?:https?://)?hangouts.google.com/[^\s]*", "Google Hangouts", false);
    public static VideoConferenceService GoogleMeet { get; } = new(ServiceIdentifier.GoogleMeet, @"(?:https?://)?meet\.google\.com/(?:_meet/)?[a-z-]+", "Google Meet", false);
    public static VideoConferenceService Zoom { get; } = new(ServiceIdentifier.Zoom, @"https?:\/\/(?:[a-zA-Z0-9-.]+)?zoom.(?:us|com|com\.cn)\/(?:j|my|w)\/[-a-zA-Z0-9()@:%_\+.~#?&=\/]*", "Zoom", true);
    public static VideoConferenceService ZoomGovernment { get; } = new(ServiceIdentifier.ZoomGovernment, @"https?:\/\/(?:[a-zA-Z0-9-.]+)?zoomgov.(?:us|com|com\.cn)\/(?:j|my|w)\/[-a-zA-Z0-9()@:%_\+.~#?&=\/]*", "Zoom for Government", true);
    public static VideoConferenceService ZoomNative { get; } = new(ServiceIdentifier.ZoomNative, @"zoommtg://([a-z0-9-.]+)?zoom\.(us|com|com\.cn)/join[-a-zA-Z0-9()@:%_\+.~#?&=\/]*", "Zoom", true);
    public static VideoConferenceService Webex { get; } = new(ServiceIdentifier.Webex, @"(?:https?://)?([a-z0-9-.]+)?webex\.com/meet/[^\s]*", "Webex", false);
    public static VideoConferenceService WebexGovernment { get; } = new(ServiceIdentifier.WebexGovernment, @"(?:https?://)?([a-z0-9-.]+)?webexgov\.com/meet/[^\s]*", "Webex for Government", false);
    public static VideoConferenceService WebexWebinar { get; } = new(ServiceIdentifier.WebexWebinar, @"(?:https?://)?([a-z0-9-.]+)?webex\.com/[^\s]*/j\.php\?", "Webex Webinar", false);
    public static VideoConferenceService MicrosoftTeams { get; } = new(ServiceIdentifier.MicrosoftTeams, @"(?:https?://)?teams\.microsoft\.com/l/meetup-join/[a-zA-Z0-9_%\/=\-\+\.?]+", "Microsoft Teams", true);
    public static VideoConferenceService MicrosoftTeamsGovernment { get; } = new(ServiceIdentifier.MicrosoftTeamsGovernment, @"(?:https?://)?(.*\.)?teams\.microsoft\.us/l/meetup-join/[a-zA-Z0-9_%\/=\-\+\.?]+", "Microsoft Teams for Government", true);
    public static VideoConferenceService GoTo { get; } = new(ServiceIdentifier.GoTo, @"(?:https?://)?([a-z0-9.]+)?gotomeeting\.com/[^\s]*", "GoToMeeting", false);
    public static VideoConferenceService GoToWebinar { get; } = new(ServiceIdentifier.GoToWebinar, @"(?:https?://)?([a-z0-9.]+)?gotowebinar\.com/[^\s]*", "GoToWebinar", false);
    public static VideoConferenceService Bluejeans { get; } = new(ServiceIdentifier.Bluejeans, @"(?:https?://)?([a-z0-9.]+)?bluejeans\.com/[^\s]*", "BlueJeans", false);
    public static VideoConferenceService JoinMe { get; } = new(ServiceIdentifier.JoinMe, @"(?:https?://)?join\.me/[^\s]*", "join.me", false);
    public static VideoConferenceService Whereby { get; } = new(ServiceIdentifier.Whereby, @"(?:https?://)?whereby\.com/[^\s]*", "Whereby", false);
    public static VideoConferenceService RingCentral { get; } = new(ServiceIdentifier.RingCentral, @"(?:https?://)?([a-z0-9.]+)?ringcentral\.com/[^\s]*", "RingCentral", false);
    public static VideoConferenceService AmazonChime { get; } = new(ServiceIdentifier.AmazonChime, @"(?:https?://)?([a-z0-9-.]+)?chime\.aws/[0-9]*", "Amazon Chime", false);
    public static VideoConferenceService FaceTime { get; } = new(ServiceIdentifier.FaceTime, @"https://facetime\.apple\.com/join[^\s]*", "FaceTime", false);
    public static VideoConferenceService Jitsi { get; } = new(ServiceIdentifier.Jitsi, @"(?:https?://)?meet\.jit\.si/[^\s]*", "Jitsi", false);
    public static VideoConferenceService YouCanBookMe { get; } = new(ServiceIdentifier.YouCanBookMe, @"https?:\/\/(?:[a-zA-Z0-9-.]+)?youcanbook.me\/(?:zoom|google-meet|microsoft-teams)\/*", "YouCanBook.me", false);
    public static VideoConferenceService Entre { get; } = new(ServiceIdentifier.Entre, @"(?:https?://)?joinentre.com/room/[^\s]*", "Entre", false);
    public static VideoConferenceService Riverside { get; } = new(ServiceIdentifier.Riverside, @"(?:https?://)?riverside.fm/studio/[^\s]*", "Riverside", false);
    public static VideoConferenceService SlackHuddle { get; } = new(ServiceIdentifier.SlackHuddle, @"(?:https?://)?app.slack.com/huddle/[A-Za-z0-9./]+", "Slack Huddle", false);
    public static VideoConferenceService MetaWorkplace { get; } = new(ServiceIdentifier.FacebookWorkspace, @"(?:https?://)?([a-z0-9-.]+)?workplace\.com/[^\s]+", "Meta Workplace ", false);
    public static VideoConferenceService Around { get; } = new(ServiceIdentifier.Around, @"(?:https?://)?around\.co/r/[^\s]*", "Around", false);
    public static VideoConferenceService TeamSpeakNative { get; } = new(ServiceIdentifier.TeamSpeak, @"ts3server://*", "TeamSpeak", true);
    public static VideoConferenceService DoxyMe { get; } = new(ServiceIdentifier.DoxyMe, @"https://doxy.me/[^\s]*", "doxy.me", false);
    public static VideoConferenceService Signal { get; } = new(ServiceIdentifier.Signal, @"https://signal.link/call/#key=[^\s]*", "Signal", false);
    public static VideoConferenceService WhatsApp { get; } = new(ServiceIdentifier.WhatsApp, @"https://call.whatsapp.com/([a-z0-9.]+)?/[^\s]*", "WhatsApp", false);
    public static VideoConferenceService Tuple { get; } = new(ServiceIdentifier.Tuple, @"https://tuple.app/c/[^\s]*", "Tuple", false);
    public static VideoConferenceService Roam { get; } = new(ServiceIdentifier.Roam, @"https://ro.am/r/#/d/[^\s]*", "Roam", false);

    public static IReadOnlyList<VideoConferenceService> All { get; } = (VideoConferenceService[])
    [
        GoogleHangouts,
        GoogleMeet,
        Zoom,
        ZoomGovernment,
        ZoomNative,
        Webex,
        WebexGovernment,
        WebexWebinar,
        MicrosoftTeams,
        MicrosoftTeamsGovernment,
        GoTo,
        GoToWebinar,
        Bluejeans,
        JoinMe,
        Whereby,
        RingCentral,
        AmazonChime,
        FaceTime,
        Jitsi,
        YouCanBookMe,
        Entre,
        Riverside,
        SlackHuddle,
        MetaWorkplace,
        Around,
        TeamSpeakNative,
        DoxyMe,
        Signal,
        WhatsApp,
        Tuple,
        Roam
    ];
}

public sealed record VideoConferenceLink(VideoConferenceService Service, Uri Url);
