namespace Wino.Core.Domain.Enums;

/// <summary>
/// How a single account capability (mail, calendar, contacts, tasks) is used.
/// The numeric values match the segment order of the capability tiles on the provider selection page.
/// </summary>
public enum AccountCapabilityMode
{
    Off = 0,
    Provider = 1,
    Local = 2
}
