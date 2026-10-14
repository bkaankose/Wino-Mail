namespace Wino.Core.Domain.Enums;

/// <summary>Why a relay registration pass runs. Pending reasons are coalesced into one pass.</summary>
public enum PushRelaySyncReason
{
    Startup,
    AccountsChanged,
    CredentialsChanged,
    PushTokenChanged,
    WinoAccountChanged,
    OptInChanged,
    Foreground,
    CheckIn,
    DeviceRevoked,
    UserRequested
}

/// <summary>Whether live notifications can work on this device right now.</summary>
public enum PushRelayAvailability
{
    /// <summary>The head registers no relay adapters, or its capabilities rule the relay out.</summary>
    UnsupportedPlatform,

    /// <summary>No Wino Account is signed in, or the API refused to issue a relay token.</summary>
    SignInRequired,

    /// <summary>An opt-in platform where the user has not turned live notifications on.</summary>
    DisabledByUser,

    Enabled,

    /// <summary>The relay rejected this platform for the current app version.</summary>
    RejectedByRelay,

    /// <summary>Repeated transient failures; passes continue with backoff.</summary>
    ServiceUnavailable
}

/// <summary>The result of handling one push payload; heads map it to their background fetch result.</summary>
public enum PushRelayNotificationOutcome
{
    Ignored,
    NoData,
    NewData,
    Failed
}

/// <summary>The result of asking the relay for a test push.</summary>
public enum PushRelayPushTestResult
{
    Queued,

    /// <summary>The device has no push token registered (for example the iOS Simulator).</summary>
    NoPushToken,

    /// <summary>Live notifications are not enabled or the device is not registered yet.</summary>
    NotRegistered,

    /// <summary>The relay allows one test per minute; this request came too soon.</summary>
    RateLimited,

    Failed
}
