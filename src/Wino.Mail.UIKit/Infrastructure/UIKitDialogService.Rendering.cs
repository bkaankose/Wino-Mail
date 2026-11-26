using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Reader;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Views.Dialogs;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>
/// The reader and composer dialogs (Windows ActionCardOpenDialog, DraftCheckDialog, PictureDescriptionDialog and
/// PictureResizeDialog; Mac AppKitDialogService.Rendering): alerts for the card and link questions, form sheets for the
/// checks and the picture forms.
/// </summary>
public sealed partial class UIKitDialogService
{
    private const int MaximumPictureSide = 4000;

    public async Task<ActionCardOpenConfirmation> ShowActionCardOpenConfirmationAsync(string actionName, string senderName, Uri address)
    {
        var host = address.IsAbsoluteUri ? address.IdnHost : address.OriginalString;
        var path = address.IsAbsoluteUri ? address.PathAndQuery + address.Fragment : string.Empty;
        var message = string.Format(CultureInfo.CurrentCulture, Translator.ActionCardOpenDialog_MessageFormat, senderName) + "\n\n" + host + path;

        var choice = await AlertAsync(
            string.Format(CultureInfo.CurrentCulture, Translator.ActionCardOpenDialog_TitleFormat, actionName),
            message,
            new List<string> { Translator.Buttons_Open, Translator.ActionCardOpenDialog_DontAskAgain },
            Translator.Buttons_Cancel);

        return choice switch
        {
            0 => new ActionCardOpenConfirmation(true, false),
            1 => new ActionCardOpenConfirmation(true, true),
            _ => new ActionCardOpenConfirmation(false, false)
        };
    }

    public async Task<DraftCheckDialogResult> ShowDraftCheckDialogAsync(IReadOnlyList<DraftCheckIssueItem> issues)
    {
        DraftCheckDialogResult? chosen = null;
        var message = issues.Count == 1
            ? Translator.DraftCheckDialog_MessageOne
            : string.Format(CultureInfo.CurrentCulture, Translator.DraftCheckDialog_MessageFormat, issues.Count);

        var answer = await PresentAsync(async presenter =>
        {
            var form = new FormSheet(Translator.DraftCheckDialog_Title, message, Translator.DraftCheckDialog_SendAnyway);
            for (var index = 0; index < issues.Count; index++)
            {
                var issue = issues[index];
                var row = index;
                var title = WinoStyle.Label(issue.Title, WinoStyle.Subheadline, WinoStyle.PrimaryText, 0);
                title.Font = UIFont.BoldSystemFontOfSize(15);
                var detail = WinoStyle.Label(issue.Detail, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
                var text = WinoLayout.VStack(2, title, detail);

                var buttons = WinoLayout.HStack(6,
                    RowButton(Translator.DraftCheckDialog_Show, issue.CanShow, false, DraftCheckIssueAction.Show, row, form, c => chosen = c),
                    RowButton(Translator.DraftCheckDialog_Describe, issue.CanDescribe, false, DraftCheckIssueAction.Describe, row, form, c => chosen = c),
                    RowButton(Translator.DraftCheckDialog_NotNeeded, issue.CanMarkNotNeeded, false, DraftCheckIssueAction.NotNeeded, row, form, c => chosen = c),
                    RowButton(Translator.DraftCheckDialog_Fix, issue.CanFix, true, DraftCheckIssueAction.Fix, row, form, c => chosen = c));
                buttons.Alignment = UIStackViewAlignment.Center;

                var content = WinoLayout.VStack(8, text, buttons);
                content.Alignment = UIStackViewAlignment.Fill;
                var surface = new WinoSurfaceView { CornerRadius = 10, Fill = UIColor.SecondarySystemGroupedBackground };
                WinoLayout.Fill(content, surface, 10, 12, 10, 12);
                form.AddFullWidth(surface);
            }

            var confirmed = await form.PresentAsync(presenter);
            return chosen ?? new DraftCheckDialogResult(confirmed ? DraftCheckDialogChoice.SendAnyway : DraftCheckDialogChoice.GoBack);
        }, new DraftCheckDialogResult(DraftCheckDialogChoice.GoBack));

        return answer;
    }

    public Task<string?> ShowPictureDescriptionDialogAsync(string? currentDescription) => PresentAsync<string?>(async presenter =>
    {
        var form = new FormSheet(Translator.PictureDescriptionDialog_Title, Translator.PictureDescriptionDialog_Message, Translator.Buttons_Save);
        var text = new UITextView
        {
            Font = UIFont.SystemFontOfSize(16),
            Text = currentDescription ?? string.Empty,
            BackgroundColor = UIColor.SecondarySystemGroupedBackground,
            TranslatesAutoresizingMaskIntoConstraints = false,
            AccessibilityLabel = Translator.PictureDescriptionDialog_Title
        };
        text.Layer.CornerRadius = 8;
        text.HeightAnchor.ConstraintEqualTo(120).Active = true;
        form.AddFullWidth(text);

        if (!await form.PresentAsync(presenter)) return null;
        return (text.Text ?? string.Empty).Trim();
    }, null);

    public Task<PictureSize?> ShowPictureResizeDialogAsync(PictureSize currentSize) => PresentAsync<PictureSize?>(async presenter =>
    {
        var form = new FormSheet(Translator.ImageResizeDialog_Title, null, Translator.ImageResizeDialog_Apply);
        var width = form.AddTextField(Translator.ImageResizeDialog_Width, currentSize.Width.ToString(CultureInfo.InvariantCulture));
        var height = form.AddTextField(Translator.ImageResizeDialog_Height, currentSize.Height.ToString(CultureInfo.InvariantCulture));
        width.KeyboardType = UIKeyboardType.NumberPad;
        height.KeyboardType = UIKeyboardType.NumberPad;
        var keepProportions = form.AddSwitch(Translator.ImageResizeDialog_KeepProportions, true);

        var ratio = currentSize.Height > 0 ? (double)currentSize.Width / currentSize.Height : 1;
        var syncing = false;
        width.EditingChanged += (_, _) =>
        {
            if (syncing || !keepProportions.On || !TryParsePictureSide(width.Text, out var value)) return;
            syncing = true;
            height.Text = Math.Max(1, Math.Round(value / ratio)).ToString(CultureInfo.InvariantCulture);
            syncing = false;
            form.Refresh();
        };
        height.EditingChanged += (_, _) =>
        {
            if (syncing || !keepProportions.On || !TryParsePictureSide(height.Text, out var value)) return;
            syncing = true;
            width.Text = Math.Max(1, Math.Round(value * ratio)).ToString(CultureInfo.InvariantCulture);
            syncing = false;
            form.Refresh();
        };
        form.CanConfirm = () => TryParsePictureSide(width.Text, out _) && TryParsePictureSide(height.Text, out _);

        if (!await form.PresentAsync(presenter)) return null;
        if (!TryParsePictureSide(width.Text, out var newWidth) || !TryParsePictureSide(height.Text, out var newHeight)) return null;
        return new PictureSize(newWidth, newHeight);
    }, null);

    private static bool TryParsePictureSide(string? text, out int value)
        => int.TryParse((text ?? string.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value is >= 1 and <= MaximumPictureSide;

    private static UIButton RowButton(string title, bool isVisible, bool primary, DraftCheckIssueAction action, int row, FormSheet form, Action<DraftCheckDialogResult> chose)
    {
        var button = SettingsBinder.CreateButton(title, primary);
        button.Hidden = !isVisible;
        button.PrimaryActionTriggered += (_, _) =>
        {
            chose(new DraftCheckDialogResult(DraftCheckDialogChoice.IssueAction, row, action));
            form.Close(true);
        };
        return button;
    }
}
