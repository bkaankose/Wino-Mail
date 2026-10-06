using AppKit;
using Wino.Mail.AppKit.Poc;

NSApplication.Init();
NSApplication.SharedApplication.Delegate = new AppDelegate();
NSApplication.Main(args);
