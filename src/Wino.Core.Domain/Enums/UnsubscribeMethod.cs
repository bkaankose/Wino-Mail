namespace Wino.Core.Domain.Enums;

/// <summary>
/// How a reader leaves a mailing list: the RFC 8058 one-click POST, the list's web page, or a mail to the list.
/// </summary>
public enum UnsubscribeMethod
{
    OneClick,
    Website,
    Mail
}
