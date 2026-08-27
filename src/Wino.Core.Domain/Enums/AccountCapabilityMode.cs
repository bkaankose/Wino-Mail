namespace Wino.Core.Domain.Enums;

/// <summary>
/// How a single account capability (mail, calendar, contacts, tasks) is used.
/// </summary>
public enum AccountCapabilityMode
{
    Off = 0,
    Provider = 1,
    Local = 2
}
