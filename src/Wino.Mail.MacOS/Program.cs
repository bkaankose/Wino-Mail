using AppKit;

namespace Wino.Mail.MacOS;

internal static class Program
{
    private static void Main(string[] args)
    {
        NSApplication.Init();
        var appDelegate = new AppDelegate();
        NSApplication.SharedApplication.Delegate = appDelegate;
        NSApplication.Main(args);
        GC.KeepAlive(appDelegate);
    }
}
