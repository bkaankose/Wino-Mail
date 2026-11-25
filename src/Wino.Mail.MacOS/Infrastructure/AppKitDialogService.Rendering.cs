using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Reader;
using Wino.Mail.MacOS.Views.Dialogs;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// The reader and composer dialogs (Windows ActionCardOpenDialog, DraftCheckDialog, PictureDescriptionDialog and
/// PictureResizeDialog): alert sheets for the link and card questions, window sheets for the checks and forms.
/// </summary>
public sealed partial class AppKitDialogService
{
    private const int MaximumPictureSide = 4000;

    public async Task<ActionCardOpenConfirmation> ShowActionCardOpenConfirmationAsync(string actionName, string senderName, Uri address)
    {
        var dontAskAgain = false;
        var host = address.IsAbsoluteUri ? address.IdnHost : address.OriginalString;
        var path = address.IsAbsoluteUri ? address.PathAndQuery + address.Fragment : string.Empty;
        var addressView = WinoStyle.Label($"{host}{path}", NSFont.MonospacedSystemFont(12, NSFontWeight.Regular), WinoStyle.PrimaryText, 0);

        var confirmed = await AlertAsync(
            string.Format(CultureInfo.CurrentCulture, Translator.ActionCardOpenDialog_TitleFormat, actionName),
            string.Format(CultureInfo.CurrentCulture, Translator.ActionCardOpenDialog_MessageFormat, senderName),
            [Translator.Buttons_Open, Translator.Buttons_Cancel],
            accessory: addressView,
            configure: alert =>
            {
                alert.ShowsSuppressionButton = true;
                alert.SuppressionButton!.Title = Translator.ActionCardOpenDialog_DontAskAgain;
            },
            completed: alert => dontAskAgain = alert.SuppressionButton?.State == NSCellStateValue.On) == 0;

        return new ActionCardOpenConfirmation(confirmed, confirmed && dontAskAgain);
    }

    public Task<DraftCheckDialogResult> ShowDraftCheckDialogAsync(IReadOnlyList<DraftCheckIssueItem> issues) => PresentAsync(window =>
    {
        var completion = new TaskCompletionSource<DraftCheckDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new DraftCheckDialogResult(DraftCheckDialogChoice.GoBack);
        var sheet = new DraftCheckSheet(new CGRect(0, 0, 520, 200), NSWindowStyle.Titled, NSBackingStore.Buffered, false);
        sheet.ReleaseWhenClosed(false);
        sheet.Title = Translator.DraftCheckDialog_Title;

        void Choose(DraftCheckDialogResult choice)
        {
            result = choice;
            window.EndSheet(sheet);
        }

        var message = WinoStyle.Label(issues.Count == 1
            ? Translator.DraftCheckDialog_MessageOne
            : string.Format(CultureInfo.CurrentCulture, Translator.DraftCheckDialog_MessageFormat, issues.Count),
            WinoStyle.Body, WinoStyle.SecondaryText, 0);
        message.PreferredMaxLayoutWidth = 440;

        var rows = new List<NSView>();
        for (var index = 0; index < issues.Count; index++)
        {
            var issue = issues[index];
            var row = index;
            var title = WinoStyle.Label(issue.Title, WinoStyle.Body, WinoStyle.PrimaryText, 0);
            title.Font = NSFont.BoldSystemFontOfSize(13);
            title.PreferredMaxLayoutWidth = 300;
            var detail = WinoStyle.Label(issue.Detail, WinoStyle.Description, WinoStyle.SecondaryText, 0);
            detail.PreferredMaxLayoutWidth = 300;
            var text = WinoLayout.VStack(2, title, detail);

            var show = DraftCheckButton(Translator.DraftCheckDialog_Show, issue.CanShow, () => Choose(new(DraftCheckDialogChoice.IssueAction, row, DraftCheckIssueAction.Show)));
            var describe = DraftCheckButton(Translator.DraftCheckDialog_Describe, issue.CanDescribe, () => Choose(new(DraftCheckDialogChoice.IssueAction, row, DraftCheckIssueAction.Describe)));
            var notNeeded = DraftCheckButton(Translator.DraftCheckDialog_NotNeeded, issue.CanMarkNotNeeded, () => Choose(new(DraftCheckDialogChoice.IssueAction, row, DraftCheckIssueAction.NotNeeded)));
            var fix = DraftCheckButton(Translator.DraftCheckDialog_Fix, issue.CanFix, () => Choose(new(DraftCheckDialogChoice.IssueAction, row, DraftCheckIssueAction.Fix)));
            var actions = WinoLayout.HStack(6, show, describe, notNeeded, fix);

            var box = WinoLayout.HStack(12, text, WinoLayout.Spacer(), actions);
            box.EdgeInsets = new NSEdgeInsets(10, 12, 10, 12);
            box.WantsLayer = true;
            box.Layer!.CornerRadius = 6;
            box.Layer.BorderWidth = 1;
            box.Layer.BorderColor = WinoStyle.ZoneStroke.CGColor;
            rows.Add(box);
        }

        var goBack = new NSButton { Title = Translator.DraftCheckDialog_GoBack, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\r" };
        goBack.Activated += (_, _) => Choose(new(DraftCheckDialogChoice.GoBack));
        var sendAnyway = new NSButton { Title = Translator.DraftCheckDialog_SendAnyway, BezelStyle = NSBezelStyle.Rounded };
        sendAnyway.Activated += (_, _) => Choose(new(DraftCheckDialogChoice.SendAnyway));
        var footer = WinoLayout.HStack(WinoStyle.Space2, WinoLayout.Spacer(), sendAnyway, goBack);

        var stack = WinoLayout.VStack(WinoStyle.Space3, message);
        foreach (var row in rows) stack.AddArrangedSubview(row);
        stack.AddArrangedSubview(footer);
        stack.EdgeInsets = new NSEdgeInsets(18, 20, 16, 20);
        foreach (var view in rows)
            view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -40).Active = true;
        footer.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -40).Active = true;
        WinoLayout.Fill(stack, sheet.ContentView!);
        sheet.Cancelled = () => Choose(new(DraftCheckDialogChoice.GoBack));

        window.BeginSheet(sheet, _ =>
        {
            completion.TrySetResult(result);
            sheet.Dispose();
        });
        return completion.Task;
    });

    public Task<string?> ShowPictureDescriptionDialogAsync(string? currentDescription) => PresentAsync<string?>(async window =>
    {
        var form = new FormSheet(Translator.PictureDescriptionDialog_Title, Translator.PictureDescriptionDialog_Message, Translator.Buttons_Save, 460);
        var text = new NSTextView
        {
            Font = NSFont.SystemFontOfSize(13),
            Editable = true,
            Selectable = true,
            RichText = false,
            VerticallyResizable = true,
            HorizontallyResizable = false,
            AutoresizingMask = NSViewResizingMask.WidthSizable,
            Value = currentDescription ?? string.Empty
        };
        text.TextContainer!.WidthTracksTextView = true;
        WinoAccessibility.Label(text, Translator.PictureDescriptionDialog_Title);
        var scroll = new NSScrollView
        {
            HasVerticalScroller = true,
            BorderType = NSBorderType.BezelBorder,
            DocumentView = text,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        scroll.HeightAnchor.ConstraintEqualTo(96).Active = true;
        form.AddFullWidth(scroll);

        if (!await form.PresentAsync(window)) return null;
        return text.Value.Trim();
    });

    public Task<PictureSize?> ShowPictureResizeDialogAsync(PictureSize currentSize) => PresentAsync<PictureSize?>(async window =>
    {
        var form = new FormSheet(Translator.ImageResizeDialog_Title, null, Translator.ImageResizeDialog_Apply, 360);
        var width = form.AddTextField(Translator.ImageResizeDialog_Width, currentSize.Width.ToString(CultureInfo.InvariantCulture));
        var height = form.AddTextField(Translator.ImageResizeDialog_Height, currentSize.Height.ToString(CultureInfo.InvariantCulture));
        var keepProportions = new NSButton { Title = Translator.ImageResizeDialog_KeepProportions, State = NSCellStateValue.On };
        keepProportions.SetButtonType(NSButtonType.Switch);
        form.AddFullWidth(keepProportions);

        var ratio = currentSize.Height > 0 ? (double)currentSize.Width / currentSize.Height : 1;
        var syncing = false;
        width.Changed += (_, _) =>
        {
            if (syncing || keepProportions.State != NSCellStateValue.On || !TryParsePictureSide(width.StringValue, out var value)) return;
            syncing = true;
            height.StringValue = Math.Max(1, Math.Round(value / ratio)).ToString(CultureInfo.InvariantCulture);
            syncing = false;
            form.Refresh();
        };
        height.Changed += (_, _) =>
        {
            if (syncing || keepProportions.State != NSCellStateValue.On || !TryParsePictureSide(height.StringValue, out var value)) return;
            syncing = true;
            width.StringValue = Math.Max(1, Math.Round(value * ratio)).ToString(CultureInfo.InvariantCulture);
            syncing = false;
            form.Refresh();
        };
        form.CanConfirm = () => TryParsePictureSide(width.StringValue, out _) && TryParsePictureSide(height.StringValue, out _);

        if (!await form.PresentAsync(window)) return null;
        if (!TryParsePictureSide(width.StringValue, out var newWidth) || !TryParsePictureSide(height.StringValue, out var newHeight)) return null;
        return new PictureSize(newWidth, newHeight);
    });

    private static bool TryParsePictureSide(string text, out int value)
        => int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value is >= 1 and <= MaximumPictureSide;

    private static NSButton DraftCheckButton(string title, bool isVisible, Action action)
    {
        var button = new NSButton { Title = title, BezelStyle = NSBezelStyle.Rounded, Hidden = !isVisible };
        button.Activated += (_, _) => action();
        return button;
    }

    /// <summary>Escape closes the checks sheet as "go back", the same as its button.</summary>
    private sealed class DraftCheckSheet(CGRect contentRect, NSWindowStyle style, NSBackingStore backing, bool defer)
        : NSWindow(contentRect, style, backing, defer)
    {
        public Action? Cancelled { get; set; }

        [Export("cancelOperation:")]
        public void CancelOperation(NSObject? sender) => Cancelled?.Invoke();
    }
}
