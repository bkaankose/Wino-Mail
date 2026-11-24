namespace Wino.Core.Domain.Enums;

/// <summary>
/// Where an unsubscribe method came from: the List-Unsubscribe header or a link in the body.
/// </summary>
public enum UnsubscribeSource
{
    Header,
    Body
}
