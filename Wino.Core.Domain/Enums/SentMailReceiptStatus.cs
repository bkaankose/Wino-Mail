namespace Wino.Core.Domain.Enums;

public enum SentMailReceiptStatus
{
    None = 0,
    Requested = 1,
    Acknowledged = 2,
    FailedToCorrelate = 3
}
