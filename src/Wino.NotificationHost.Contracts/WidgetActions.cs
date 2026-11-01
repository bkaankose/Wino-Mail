using System.Net;

namespace Wino.NotificationHost.Contracts;

/// <summary>
/// Widget actions reach the main application as a forwarded notification activation whose argument carries these
/// keys, in the same "key=value&amp;key=value" form as toast arguments.
/// </summary>
public static class WidgetActions
{
    public const string ActionKey = "WinoWidgetAction";
    public const string ValueKey = "WinoWidgetValue";

    public const string OpenMail = "openMail";
    public const string OpenInbox = "openInbox";
    public const string NewMail = "newMail";
    public const string OpenEvent = "openEvent";
    public const string JoinEvent = "joinEvent";
    public const string NewEvent = "newEvent";
    public const string OpenTasks = "openTasks";
    public const string AddTask = "addTask";
    public const string OpenContact = "openContact";
    public const string EmailContact = "emailContact";
    public const string OpenWino = "openWino";
    public const string EnableWidgets = "enableWidgets";

    public static bool IsKnown(string? action) => action is
        OpenMail or OpenInbox or NewMail or OpenEvent or JoinEvent or NewEvent or
        OpenTasks or AddTask or OpenContact or EmailContact or OpenWino or EnableWidgets;

    public static string CreateArgument(string action, string? value)
        => string.IsNullOrEmpty(value)
            ? $"{ActionKey}={WebUtility.UrlEncode(action)}"
            : $"{ActionKey}={WebUtility.UrlEncode(action)}&{ValueKey}={WebUtility.UrlEncode(value)}";
}
