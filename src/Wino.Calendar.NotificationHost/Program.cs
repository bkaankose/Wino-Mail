using System;
using Wino.NotificationHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => NotificationHostRuntime.Run(args);
}
