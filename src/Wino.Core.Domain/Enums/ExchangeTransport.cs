namespace Wino.Core.Domain.Enums;

/// <summary>
/// How an on-premises Exchange account talks to its server. MAPI over HTTP needs Exchange 2013 SP1 or newer with the
/// protocol enabled; older or locked-down servers only offer Exchange Web Services. As a preference, Automatic means
/// MAPI over HTTP when Autodiscover advertises it and EWS otherwise. As a detection result, it means not detected yet.
/// The values are stored, so they must not change.
/// </summary>
public enum ExchangeTransport
{
    Automatic = 0,
    MapiHttp = 1,
    Ews = 2,
    ActiveSync = 3
}
