using AppKit;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Import from other mail clients (Windows MailClientImportDialog) as a sheet. The sandbox cannot read
/// ~/Library/Thunderbird, so the user first chooses the Thunderbird folder in an open panel, and the scan reads that
/// folder through the access the panel grants.
/// </summary>
public sealed partial class AppKitDialogService
{
#if DEBUG
    /// <summary>Set by the debug bridge to scan a folder in place of the open panel.</summary>
    internal static string? DebugThunderbirdFolder { get; set; }
#endif

    public async Task<List<MailClientImportCandidate>?> ShowMailClientImportDialogAsync()
    {
        if (services?.GetService<IMailClientAccountImportService>() is not { } importService) return null;

        var folder = await ChooseThunderbirdFolderAsync();
        if (folder is null) return null;

        var scoped = folder.StartAccessingSecurityScopedResource();
        try
        {
            var model = new MailClientImportDialogModel(importService);
            return await PresentAsync(window => PresentMailClientImport(window, model, folder.Path ?? string.Empty));
        }
        finally
        {
            if (scoped) folder.StopAccessingSecurityScopedResource();
        }
    }

    private async Task<NSUrl?> ChooseThunderbirdFolderAsync()
    {
#if DEBUG
        if (DebugThunderbirdFolder is { } debugFolder) return NSUrl.FromFilename(debugFolder);
#endif
        return await PresentAsync(PickThunderbirdFolder);
    }

    private static Task<NSUrl?> PickThunderbirdFolder(NSWindow window)
    {
        var completion = new TaskCompletionSource<NSUrl?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var panel = NSOpenPanel.OpenPanel;
        panel.CanChooseDirectories = true;
        panel.CanChooseFiles = false;
        panel.AllowsMultipleSelection = false;
        panel.CanCreateDirectories = false;
        panel.Message = Translator.MailClientImport_ThunderbirdFolderMessage;
        // The sandbox's home is its container, so the panel starts in the account's real Library folder.
        panel.DirectoryUrl = NSUrl.FromFilename(Path.Combine("/Users", Environment.UserName, "Library", "Thunderbird"));

        var closing = NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.WillCloseNotification, _ => panel.Cancel(null), window);
        panel.BeginSheet(window, response =>
        {
            NSNotificationCenter.DefaultCenter.RemoveObserver(closing);
            closing.Dispose();
            completion.TrySetResult((long)response == 1 ? panel.Urls.FirstOrDefault() : null);
            panel.Dispose();
        });
        return completion.Task;
    }

    private Task<List<MailClientImportCandidate>?> PresentMailClientImport(NSWindow window, MailClientImportDialogModel model, string thunderbirdFolder)
    {
        var completion = new TaskCompletionSource<List<MailClientImportCandidate>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new CancellationTokenSource();
        var sheet = new NSWindow(new CGRect(0, 0, 480, 460), NSWindowStyle.Titled | NSWindowStyle.Resizable, NSBackingStore.Buffered, false);
        sheet.ReleaseWhenClosed(false);
        sheet.MinSize = new CGSize(420, 340);

        var scanning = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        var error = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        var empty = WinoStyle.Label(Translator.MailClientImport_NoAccounts, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        var notice = WinoStyle.Label(Translator.MailClientImport_SignInNotice, WinoStyle.Description, WinoStyle.SecondaryText, 0);

        var document = new WinoSurfaceView { TranslatesAutoresizingMaskIntoConstraints = false };
        var rows = WinoLayout.VStack(WinoStyle.Space3);
        rows.TranslatesAutoresizingMaskIntoConstraints = false;
        document.AddSubview(rows);
        NSLayoutConstraint.ActivateConstraints(
        [
            rows.TopAnchor.ConstraintEqualTo(document.TopAnchor, 4),
            rows.LeadingAnchor.ConstraintEqualTo(document.LeadingAnchor, 4),
            rows.TrailingAnchor.ConstraintEqualTo(document.TrailingAnchor, -4),
            rows.BottomAnchor.ConstraintLessThanOrEqualTo(document.BottomAnchor, -4)
        ]);
        var scroll = new NSScrollView { DocumentView = document, HasVerticalScroller = true, DrawsBackground = false, BorderType = NSBorderType.BezelBorder, TranslatesAutoresizingMaskIntoConstraints = false };
        document.WidthAnchor.ConstraintEqualTo(scroll.ContentView.WidthAnchor).Active = true;
        var rowsBuilt = false;

        var cancel = new NSButton { Title = Translator.Buttons_Cancel, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\u001b" };
        var import = new NSButton { Title = Translator.Buttons_Import, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\r", Enabled = false };

        void Finish(List<MailClientImportCandidate>? result)
        {
            cancellation.Cancel();
            if (completion.TrySetResult(result)) window.EndSheet(sheet);
        }

        void Refresh()
        {
            scanning.StringValue = model.ScanStatus;
            scanning.Hidden = !model.IsScanning;
            error.StringValue = model.ErrorMessage;
            error.Hidden = model.ErrorMessage.Length == 0;
            empty.Hidden = !model.IsNoAccountsMessageVisible;
            scroll.Hidden = !model.IsAccountListVisible;
            notice.Hidden = !model.IsAccountListVisible;
            if (model.IsAccountListVisible && !rowsBuilt)
            {
                rowsBuilt = true;
                foreach (var item in model.Items) rows.AddArrangedSubview(ImportRow(item, () => import.Enabled = model.CanImport));
            }
            import.Hidden = !model.IsAccountListVisible;
            import.Enabled = model.CanImport;
        }

        void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => dispatcher.ExecuteOnUIThread(Refresh);
        model.PropertyChanged += OnModelChanged;

        cancel.Activated += (_, _) => Finish(null);
        import.Activated += (_, _) => Finish(model.GetSelectedCandidates());

        var title = WinoStyle.Label(Translator.MailClientImport_Title, WinoStyle.Heading);
        var description = WinoStyle.Label(Translator.MailClientImport_Description, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        var buttons = WinoLayout.HStack(WinoStyle.Space2, WinoLayout.Spacer(), cancel, import);
        var stack = WinoLayout.VStack(WinoStyle.Space3, title, description, scanning, error, empty, scroll, notice, buttons);
        stack.EdgeInsets = new NSEdgeInsets(18, 20, 16, 20);
        foreach (var view in new NSView[] { description, scanning, error, empty, scroll, notice, buttons }) view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -40).Active = true;
        scroll.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);
        if (sheet.ContentView is { } content) WinoLayout.Fill(stack, content);

        window.BeginSheet(sheet, _ =>
        {
            model.PropertyChanged -= OnModelChanged;
            completion.TrySetResult(null);
            cancellation.Dispose();
            sheet.Dispose();
        });

        Refresh();
        _ = model.ScanAsync(cancellation.Token, new[] { thunderbirdFolder });
        return completion.Task;
    }

    private static NSView ImportRow(MailClientImportItem item, Action selectionChanged)
    {
        var checkbox = WinoCheckbox.Create(null);
        checkbox.State = item.IsSelected ? NSCellStateValue.On : NSCellStateValue.Off;
        checkbox.Enabled = item.CanSelect;
        checkbox.Activated += (_, _) =>
        {
            item.IsSelected = checkbox.State == NSCellStateValue.On;
            selectionChanged();
        };

        var icon = new WinoIconView(item.IconGlyph, 24, WinoStyle.PrimaryText);
        var title = WinoStyle.Label(item.Title, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 1);
        var subtitle = WinoStyle.Label(item.Subtitle, WinoStyle.Caption, WinoStyle.SecondaryText, 1);
        var text = WinoLayout.VStack(2, title, subtitle);
        text.Alignment = NSLayoutAttribute.Leading;

        var row = WinoLayout.HStack(10, checkbox, icon, text);
        row.Alignment = NSLayoutAttribute.CenterY;
        return row;
    }
}
