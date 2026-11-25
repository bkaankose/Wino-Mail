using System;
using System.Collections.Generic;
using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Reader;
using Wino.Mail.ViewModels;
using Wino.Renderer;
using Wino.Renderer.Primitives;
using Wino.Renderer.Selection;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// The reader's menus (Windows MailRenderingPage context menu, Mac MailRenderingPageViewController.ContextMenu): the engine
/// reports the targets under the press, and the page shows an action sheet in the message's own theme. A tap on an
/// underlined date opens the date sheet.
/// </summary>
public sealed partial class MailRenderingPageViewController
{
    private UITapGestureRecognizer? _dateTap;

    private void InitializeDateTap()
    {
        _dateTap = new UITapGestureRecognizer(MailView_Tapped);
        _mailView.AddGestureRecognizer(_dateTap);
    }

    private void MailView_Tapped()
    {
        if (_dateTap is null || _mailView.Reader is not { } reader) return;

        var location = _dateTap.LocationInView(_mailView);
        if (reader.HitTest(new PxPoint((float)location.X, (float)location.Y)).Link is not null) return;
        if (_reader?.GetDateAt(location.X, location.Y) is not { } date) return;

        var sheet = CreateSheet(null);
        AddDateItems(sheet, date);
        AddItem(sheet, Translator.Buttons_Cancel, null, UIAlertActionStyle.Cancel);
        PresentSheet(sheet, location);
    }

    private void MailView_ContextMenuRequested(object? sender, ContextMenuRequest request)
    {
        var sheet = CreateSheet(request.LinkUrl);
        var targets = request.Targets;

        foreach (var target in targets)
        {
            if (target.Kind == ContextTargetKind.Date && _reader?.GetDate(target.Index) is { } date)
            {
                AddDateItems(sheet, date);
                break;
            }
        }

        if (request.HasSelection)
            AddItem(sheet, Translator.Buttons_Copy, () => _mailView.CopySelection());

        if (!string.IsNullOrEmpty(request.LinkUrl))
        {
            var url = request.LinkUrl;
            if (Uri.TryCreate(url, UriKind.Absolute, out var address))
                AddItem(sheet, Translator.Buttons_Open, () => Observe(OpenAsync(new ResolvedMailLink(address, null))));
            AddItem(sheet, Translator.Reader_ContextMenu_CopyLink, () => Observe(ViewModel.CopyLinkCommand.ExecuteAsync(url)));
        }

        if (request.OnImage && _mailView.Reader is { } imageReader && imageReader.TryGetImageAt(request.ViewPoint, out var image) && !image.Bytes.IsEmpty)
            AddItem(sheet, Translator.Reader_ContextMenu_CopyImage, () => CopyImage(image));

        foreach (var target in targets)
        {
            if (target.Kind != ContextTargetKind.Table) continue;
            AddItem(sheet, Translator.Reader_ContextMenu_CopyTable, () => CopyTable(target, TableTextFormat.TabSeparated));
            AddItem(sheet, Translator.Reader_ContextMenu_CopyTableAsCsv, () => CopyTable(target, TableTextFormat.Csv));
            break;
        }

        AddItem(sheet, Translator.Reader_ContextMenu_SelectAll, () => _mailView.SelectAll());
        AddItem(sheet, Translator.Buttons_Cancel, null, UIAlertActionStyle.Cancel);
        PresentSheet(sheet, new CGPoint(request.ViewPoint.X, request.ViewPoint.Y));
    }

    private UIAlertController CreateSheet(string? title)
    {
        var sheet = UIAlertController.Create(title, null, UIAlertControllerStyle.ActionSheet);
        sheet.OverrideUserInterfaceStyle = _dark ? UIUserInterfaceStyle.Dark : UIUserInterfaceStyle.Light;
        return sheet;
    }

    private static void AddItem(UIAlertController sheet, string title, Action? action, UIAlertActionStyle style = UIAlertActionStyle.Default)
    {
        var item = UIAlertAction.Create(title, style, action is null ? null : _ => action());
        sheet.AddAction(item);
    }

    private void AddDateItems(UIAlertController sheet, MailDetectedDate date)
    {
        var text = MailRenderingPageViewModel.FormatDetectedDate(date);
        sheet.Title = text;
        AddItem(sheet, Translator.Reader_Date_AddToCalendar, () => Observe(ViewModel.AddDateToCalendarCommand.ExecuteAsync(date)));
        AddItem(sheet, Translator.Reader_Date_CreateTask, () => Observe(ViewModel.CreateTaskForDateCommand.ExecuteAsync(date)));
        AddItem(sheet, Translator.Reader_Date_Copy, () => Observe(ViewModel.CopyClipboardCommand.ExecuteAsync(text)));
    }

    private void PresentSheet(UIAlertController sheet, CGPoint point)
    {
        if (sheet.PopoverPresentationController is { } popover)
        {
            popover.SourceView = _mailView;
            var anchor = _mailView.ConvertPointFromView(point, _mailView.ScrollView);
            popover.SourceRect = new CGRect(anchor.X + _mailView.ScrollView.ContentOffset.X, anchor.Y + _mailView.ScrollView.ContentOffset.Y, 1, 1);
        }

        PresentViewController(sheet, true, null);
    }

    // Copy table carries the cells as tab-separated text and the table as HTML; Copy as CSV carries the CSV text only.
    private void CopyTable(ContextTarget target, TableTextFormat format)
    {
        try
        {
            if (_mailView.Reader?.GetTableContent(target, format) is not { } content || string.IsNullOrEmpty(content.PlainText))
                return;

            if (format == TableTextFormat.TabSeparated && !string.IsNullOrEmpty(content.Html))
            {
                UIPasteboard.General.Items =
                [
                    NSDictionary<NSString, NSObject>.FromObjectsAndKeys(
                        [new NSString(content.PlainText), new NSString(content.Html)],
                        [new NSString("public.utf8-plain-text"), new NSString("public.html")])
                ];
            }
            else
            {
                UIPasteboard.General.String = content.PlainText;
            }
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }

    private void CopyImage(MailResource image)
    {
        try
        {
            using var data = NSData.FromArray(image.Bytes.ToArray());
            if (UIImage.LoadFromData(data) is { } picture)
                UIPasteboard.General.Image = picture;
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }
}
