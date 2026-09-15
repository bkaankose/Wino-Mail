namespace Wino.Core.Domain.Enums;

/// <summary>
/// How an on-premises Exchange account talks to its server. MAPI/HTTP needs Exchange 2013 SP1 or
/// newer with the protocol enabled; older or locked-down servers only offer Exchange Web Services.
/// As a preference, Automatic means "MAPI/HTTP when Autodiscover advertises it, EWS otherwise".
/// As a detection result, Automatic means "not detected yet".
/// </summary>
public enum ExchangeTransport
{
    Automatic = 0,
    MapiHttp = 1,
    Ews = 2
}
