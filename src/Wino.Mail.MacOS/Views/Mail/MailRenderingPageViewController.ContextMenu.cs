using System;
using System.Collections.Generic;
using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Reader;
using Wino.Mail.ViewModels;
using Wino.Renderer;
using Wino.Renderer.Primitives;
using Wino.Renderer.Selection;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// The reader's menus (Windows MailRenderingPage context menu): the engine reports the targets under the pointer, and the
/// page builds an NSMenu drawn in the message's own appearance. A click on an underlined date opens the date menu.
/// </summary>
public sealed partial class MailRenderingPageViewController
{
    private void MailView_Clicked()
    {
        if (NSApplication.SharedApplication.CurrentEvent is not { } clickEvent) return;

        var location = _mailView.ConvertPointFromView(clickEvent.LocationInWindow, null);
        if (_mailView.Reader is not { } reader || reader.HitTest(new PxPoint((float)location.X, (float)location.Y)).Link is not null) return;
        if (_reader?.GetDateAt(location.X, location.Y) is not { } date) return;

        var menu = CreateMenu();
        AddDateItems(menu, date);
        menu.PopUpMenu(null, location, _mailView);
    }

    private void MailView_ContextMenuRequested(object? sender, ContextMenuRequest request)
    {
        var menu = CreateMenu();

        var targets = request.Targets;
        foreach (var target in targets)
        {
            if (target.Kind == ContextTargetKind.Date && _reader?.GetDate(target.Index) is { } date)
            {
                AddDateItems(menu, date);
                break;
            }
        }

        if (request.HasSelection)
            menu.AddItem(new NSMenuItem(Translator.Buttons_Copy, (_, _) => _mailView.CopySelection()));

        if (!string.IsNullOrEmpty(request.LinkUrl))
        {
            var url = request.LinkUrl;
            menu.AddItem(new NSMenuItem(Translator.Reader_ContextMenu_CopyLink, (_, _) => Observe(ViewModel.CopyLinkCommand.ExecuteAsync(url))));
        }

        if (request.OnImage && _mailView.Reader is { } imageReader && imageReader.TryGetImageAt(request.ViewPoint, out var image) && !image.Bytes.IsEmpty)
            menu.AddItem(new NSMenuItem(Translator.Reader_ContextMenu_CopyImage, (_, _) => CopyImage(image)));

        AddTableItems(menu, targets);

        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(new NSMenuItem(Translator.Reader_ContextMenu_SelectAll, (_, _) => _mailView.SelectAll()));

        menu.PopUpMenu(null, new CGPoint(request.ViewPoint.X, request.ViewPoint.Y), _mailView);
    }

    private NSMenu CreateMenu()
    {
        var menu = new NSMenu { AutoEnablesItems = false };
        menu.Appearance = NSAppearance.GetAppearance(_dark ? NSAppearance.NameDarkAqua : NSAppearance.NameAqua);
        return menu;
    }

    private void AddDateItems(NSMenu menu, MailDetectedDate date)
    {
        var text = MailRenderingPageViewModel.FormatDetectedDate(date);

        var header = new NSMenuItem(text) { Enabled = false };
        menu.AddItem(header);
        menu.AddItem(new NSMenuItem(Translator.Reader_Date_AddToCalendar, (_, _) => Observe(ViewModel.AddDateToCalendarCommand.ExecuteAsync(date))));
        menu.AddItem(new NSMenuItem(Translator.Reader_Date_CreateTask, (_, _) => Observe(ViewModel.CreateTaskForDateCommand.ExecuteAsync(date))));
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(new NSMenuItem(Translator.Reader_Date_Copy, (_, _) => Observe(ViewModel.CopyClipboardCommand.ExecuteAsync(text))));
        menu.AddItem(NSMenuItem.SeparatorItem);
    }

    private void AddTableItems(NSMenu menu, IReadOnlyList<ContextTarget> targets)
    {
        foreach (var target in targets)
        {
            if (target.Kind != ContextTargetKind.Table) continue;

            menu.AddItem(new NSMenuItem(Translator.Reader_ContextMenu_CopyTable, (_, _) => CopyTable(target, TableTextFormat.TabSeparated)));
            menu.AddItem(new NSMenuItem(Translator.Reader_ContextMenu_CopyTableAsCsv, (_, _) => CopyTable(target, TableTextFormat.Csv)));
            return;
        }
    }

    // Copy table carries the cells as tab-separated text and the table as HTML; Copy as CSV carries the CSV text only.
    private void CopyTable(ContextTarget target, TableTextFormat format)
    {
        try
        {
            if (_mailView.Reader?.GetTableContent(target, format) is not { } content || string.IsNullOrEmpty(content.PlainText))
                return;

            var pasteboard = NSPasteboard.GeneralPasteboard;
            pasteboard.ClearContents();
            pasteboard.SetStringForType(content.PlainText, NSPasteboard.NSPasteboardTypeString);
            if (format == TableTextFormat.TabSeparated && !string.IsNullOrEmpty(content.Html))
                pasteboard.SetStringForType(content.Html, new NSString("public.html"));
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
            using var picture = new NSImage(data);
            var pasteboard = NSPasteboard.GeneralPasteboard;
            pasteboard.ClearContents();
            pasteboard.WriteObjects([picture]);
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }
}
