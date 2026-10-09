using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Dialogs;

/// <summary>
/// The signature editor (Windows SignatureEditorDialog) as a 640pt window sheet: heading and account
/// line, the name field, a one-row Format toolbar in the composer vocabulary over the composer's own
/// HTML editor session, then the images hint, Cancel and Save. Save stays disabled while the name is
/// empty or the editor is still loading; Escape cancels and Cmd+Return saves (Return stays in the editor).
/// Build on the main thread, then call <see cref="PresentAsync"/> once.
/// </summary>
internal sealed class SignatureEditorSheet
{
    private const double Width = 640;
    private const double EditorHeight = 232;

    private readonly NSWindow _sheet;
    private readonly NSTextField _name;
    private readonly NSButton _save;
    private readonly NSButton _cancel;
    private readonly WinoRichTextEditorView _editor;
    private readonly string _html;
    private readonly Action<Exception>? _error;
    private bool _busy;
    private bool _presented;
    private bool _disposed;

    public SignatureEditorSheet(string? accountLine, string? name, string? html, IExternalLauncher? launcher, IPreferencesService? preferences, Action<Exception>? error)
    {
        _html = html ?? string.Empty;
        _error = error;

        _sheet = new NSWindow(new CGRect(0, 0, Width, 480), NSWindowStyle.Titled, NSBackingStore.Buffered, false) { Title = Translator.SignatureEditorDialog_Title };
        _sheet.ReleaseWhenClosed(false);

        // ---- Heading ----
        var heading = WinoStyle.Label(Translator.SignatureEditorDialog_Title, WinoStyle.Heading);
        var account = WinoStyle.Label(accountLine, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText);
        account.Hidden = string.IsNullOrWhiteSpace(accountLine);
        var headingStack = WinoLayout.VStack(2, heading, account);
        headingStack.Alignment = NSLayoutAttribute.Leading;

        // ---- Name ----
        var nameCaption = WinoStyle.Label(Translator.SignatureEditorDialog_SignatureName_TitleNew, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText);
        _name = new NSTextField
        {
            StringValue = name?.Trim() ?? string.Empty,
            PlaceholderString = Translator.SignatureEditorDialog_SignatureName_Placeholder,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _name.WidthAnchor.ConstraintEqualTo(300).Active = true;
        WinoAccessibility.Label(_name, Translator.SignatureEditorDialog_SignatureName_TitleNew);
        _name.Changed += (_, _) => Refresh();
        var nameStack = WinoLayout.VStack(4, nameCaption, _name);
        nameStack.Alignment = NSLayoutAttribute.Leading;

        // ---- Format toolbar and editor (the composer's HTML editor session) ----
        _editor = new WinoRichTextEditorView(new WinoRichTextEditorOptions(), Translator.SettingsSignature_Title, launcher, preferences, error);
        _editor.EditorSurface.HeightAnchor.ConstraintEqualTo((nfloat)EditorHeight).Active = true;
        _editor.ReadyChanged += (_, _) => Refresh();

        // ---- Footer ----
        var hint = WinoStyle.Label(Translator.SignatureEditorDialog_ImagesHint, WinoStyle.Caption, WinoStyle.TertiaryText);
        hint.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        hint.LineBreakMode = NSLineBreakMode.TruncatingTail;
        _cancel = new NSButton { Title = Translator.Buttons_Cancel, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\u001b", TranslatesAutoresizingMaskIntoConstraints = false };
        _save = SettingsBinder.CreateButton(Translator.Buttons_Save, primary: true);
        _save.KeyEquivalent = "\r";
        _save.KeyEquivalentModifierMask = NSEventModifierMask.CommandKeyMask;
        var footer = WinoLayout.HStack(WinoStyle.Space2, hint, WinoLayout.Spacer(), _cancel, _save);

        var stack = WinoLayout.VStack(14, headingStack, nameStack, _editor, footer);
        stack.Alignment = NSLayoutAttribute.Leading;
        stack.EdgeInsets = new NSEdgeInsets(20, 20, 20, 20);
        foreach (var view in new NSView[] { _editor, footer })
            view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -40).Active = true;
        stack.WidthAnchor.ConstraintEqualTo((nfloat)Width).Active = true;
        WinoLayout.Fill(stack, _sheet.ContentView!);
        _sheet.SetContentSize(stack.FittingSize);
        _sheet.InitialFirstResponder = _name;
        Refresh();
    }

    /// <summary>Shows the sheet; returns the trimmed name and the editor HTML, or null when cancelled.</summary>
    public Task<(string Name, string HtmlBody)?> PresentAsync(NSWindow parent)
    {
        if (_presented) throw new InvalidOperationException("The signature editor is presented once.");
        _presented = true;
        var completion = new TaskCompletionSource<(string Name, string HtmlBody)?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Finish((string, string)? result)
        {
            if (!completion.TrySetResult(result)) return;
            parent.EndSheet(_sheet);
        }

        _cancel.Activated += (_, _) => Finish(null);
        _save.Activated += async (_, _) =>
        {
            if (!CanSave) return;
            _busy = true;
            Refresh();
            try
            {
                var html = await _editor.GetHtmlBodyAsync() ?? string.Empty;
                Finish((_name.StringValue.Trim(), html));
            }
            catch (Exception exception)
            {
                _error?.Invoke(exception);
                _busy = false;
                Refresh();
            }
        };

        NSObject? closing = null;
        closing = NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.WillCloseNotification, _ => Finish(null), parent);
        parent.BeginSheet(_sheet, _ =>
        {
            if (closing is not null) { NSNotificationCenter.DefaultCenter.RemoveObserver(closing); closing.Dispose(); closing = null; }
            completion.TrySetResult(null);
            Observe(DisposeAsync());
        });

        Observe(_editor.LoadAsync(_html, WinoRichTextEditorView.IsDark(parent.EffectiveAppearance)));
        return completion.Task;
    }

    private bool CanSave => _editor.IsReady && !_busy && !string.IsNullOrWhiteSpace(_name.StringValue);

    private void Refresh()
    {
        _save.Enabled = CanSave;
        _cancel.Enabled = !_busy;
    }

    private async Task DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await _editor.DisposeAsync(); }
        finally { OnMain(() => _sheet.Dispose()); }
    }

    // ---- Helpers ----

    private async void Observe(Task task)
    {
        try { await task; }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _error?.Invoke(exception); }
    }

    private static void OnMain(Action action) => NSApplication.SharedApplication.BeginInvokeOnMainThread(action);
}
