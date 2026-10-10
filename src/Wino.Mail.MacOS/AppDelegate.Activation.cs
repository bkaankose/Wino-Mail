using AppKit;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.MacOS.Infrastructure;
#if DEBUG
using Wino.Core.Domain.Interfaces;
using Wino.Platform.MacOS.Services;
#endif

namespace Wino.Mail.MacOS;

/// <summary>
/// OS activation: URL schemes (mailto, wino, webcal, webcals) and documents opened from Finder
/// (.ics, .vcf, and .eml, which opens read-only in its own reader window). Other files dropped on the Dock
/// icon, and files sent from the Services menu, become attachments of a new mail. AppKit can deliver them
/// before DidFinishLaunching, so they wait in a queue until the services exist and are then routed by
/// <see cref="MacActivationCoordinator"/>.
/// </summary>
public sealed partial class AppDelegate
{
    private readonly List<NSUrl> _pendingActivations = [];
    private MacActivationCoordinator? _activationCoordinator;

    partial void ActivationLaunching()
    {
        // The notification delegate must be in place before launching finishes so a tap that
        // launched the app is delivered (AppDelegate.Notifications.cs).
        InstallNotificationResponseHandler();
        NSApplication.SharedApplication.ServicesProvider = this;
    }

    partial void ActivationServicesReady()
    {
        if (_services is null) return;
        _activationCoordinator = _services.GetRequiredService<MacActivationCoordinator>();
#if DEBUG
        RegisterActivationDebugCommands(_activationCoordinator);
#endif
        if (_pendingActivations.Count == 0) return;
        var pending = _pendingActivations.ToArray();
        _pendingActivations.Clear();
        Observe(_activationCoordinator.HandleAsync(pending));
    }

    public override void OpenUrls(NSApplication application, NSUrl[] urls) => QueueActivation(urls);

    // Not called while OpenUrls is implemented; kept for older AppKit routes that open by path.
    public override void OpenFiles(NSApplication sender, string[] filenames)
    {
        QueueActivation(filenames.Select(path => NSUrl.FromFilename(path)).ToArray());
        sender.ReplyToOpenOrPrint(NSApplicationDelegateReply.Success);
    }

    /// <summary>The Services menu entry declared in Info.plist (NSServices, NSMessage newMailWithFiles).</summary>
    [Export("newMailWithFiles:userData:error:")]
    public void NewMailWithFiles(NSPasteboard pasteboard, NSString? userData, IntPtr error)
    {
        var options = new NSDictionary(new NSString("NSPasteboardURLReadingFileURLsOnlyKey"), NSNumber.FromBoolean(true));
        var urls = pasteboard.ReadObjectsForClasses([new ObjCRuntime.Class(typeof(NSUrl))], options)?.OfType<NSUrl>().ToArray() ?? [];
        if (urls.Length == 0 || _terminating) return;
        if (_activationCoordinator is null || !_runtimeStarted)
        {
            _pendingActivations.AddRange(urls);
            return;
        }
        Observe(_activationCoordinator.AttachFilesAsync(urls));
    }

    /// <summary>Wino has no untitled document; a Dock click must not ask for one.</summary>
    public override bool ApplicationShouldOpenUntitledFile(NSApplication sender) => false;

    private void QueueActivation(NSUrl[] urls)
    {
        if (urls.Length == 0 || _terminating) return;
        if (_activationCoordinator is null || !_runtimeStarted)
        {
            _pendingActivations.AddRange(urls);
            return;
        }
        Observe(_activationCoordinator.HandleAsync(urls));
    }

#if DEBUG
    private void RegisterActivationDebugCommands(MacActivationCoordinator coordinator)
    {
        MacDebugBridge.Register("activate", async args =>
        {
            var value = string.Join(' ', args);
            var url = NSUrl.FromString(value);
            if (url is null) return "invalid url";
            await coordinator.HandleAsync([url]);
            return "ok " + MacActivationCoordinator.Classify(url);
        });
        MacDebugBridge.Register("openfile", async args =>
        {
            var path = string.Join(' ', args);
            if (!File.Exists(path)) return "no file " + path;
            var url = NSUrl.FromFilename(path);
            await coordinator.HandleAsync([url]);
            return "ok " + MacActivationCoordinator.Classify(url);
        });
        Views.Mail.EmlReaderWindow.RegisterDebugCommands(coordinator, _services!.GetRequiredService<IDispatcher>());
        MacDebugBridge.Register("login-status", async _ =>
        {
            var behavior = await _services!.GetRequiredService<IStartupIntegrationService>().GetCurrentBehaviorAsync();
            return $"status={MacStartupIntegrationService.DescribeStatus()} behavior={behavior}";
        });
    }
#endif
}
