namespace Wino.NotificationHost.Contracts;

public enum NotificationHostOperation : byte
{
    Show = 1,
    RemoveByTag = 2,
    RemoveByTagAndGroup = 3,
    RemoveGroup = 4,
    RemoveAll = 5
}
