using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Models.Calendar;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Single calendar picker. Owned by the core dialogs work (WS4).</summary>
public sealed partial class AppKitDialogService
{
    /// <summary>
    /// Windows SingleCalendarPickerDialog: account headings with their calendars (colour dot), read-only
    /// calendars visible but disabled, and the "Open Calendar settings" link that closes the sheet.
    /// </summary>
    public async Task<AccountCalendarPickingResult> ShowSingleCalendarPickerDialogAsync(List<CalendarPickerAccountGroup> availableCalendarGroups)
    {
        var navigateToSettings = false;
        var roots = (availableCalendarGroups ?? [])
            .Select(group => new CalendarPickerNode(AccountHeading(group), null,
                (group.Calendars ?? []).Select(calendar => new CalendarPickerNode(CalendarText(calendar), calendar, [])).ToList()))
            .ToList();

        var picked = await PickSheetAsync(Translator.CalendarEventCompose_PickCalendarTitle, Translator.CalendarEventCompose_DefaultCalendarHint, roots,
            node => node.Children,
            node => node.Text,
            node => node.Calendar is { } calendar ? ColorDot(WinoStyle.FromHexString(calendar.BackgroundColorHex) ?? WinoStyle.Accent) : null,
            _ => null,
            node => node.Calendar is { IsReadOnly: false },
            Translator.Buttons_OK,
            node => node.Calendar is { IsReadOnly: true } ? Translator.CalendarPicker_ReadOnly : null,
            footer: finish =>
            {
                var link = new NSButton { Bordered = false, BezelStyle = NSBezelStyle.Inline };
                link.AttributedTitle = new NSAttributedString(Translator.CalendarEventCompose_DefaultCalendarSettingsLink, new NSStringAttributes
                {
                    ForegroundColor = WinoStyle.Accent,
                    Font = WinoStyle.Body,
                });
                WinoAccessibility.Label(link, Translator.CalendarEventCompose_DefaultCalendarSettingsLink);
                link.Activated += (_, _) => { navigateToSettings = true; finish(); };
                return link;
            });

        return new AccountCalendarPickingResult(picked?.Calendar, navigateToSettings);
    }

    private static string AccountHeading(CalendarPickerAccountGroup group)
    {
        var name = group.Account?.Name ?? string.Empty;
        var address = group.Account?.Address ?? string.Empty;
        return string.IsNullOrWhiteSpace(address) || address == name ? name : $"{name} ({address})";
    }

    private static string CalendarText(AccountCalendar calendar)
        => calendar.IsReadOnly ? $"{calendar.Name}  ·  {Translator.CalendarPicker_ReadOnly}" : calendar.Name ?? string.Empty;

    /// <summary>A filled circle drawn at display time, so dynamic colours follow the appearance.</summary>
    private static NSImage ColorDot(NSColor color, double size = 12)
        => NSImage.ImageWithSize(new CGSize(size, size), false, rect =>
        {
            color.SetFill();
            NSBezierPath.FromOvalInRect(rect.Inset(1, 1)).Fill();
            return true;
        });

    private sealed class CalendarPickerNode(string text, AccountCalendar? calendar, IReadOnlyList<CalendarPickerNode> children)
    {
        public string Text { get; } = text;
        public AccountCalendar? Calendar { get; } = calendar;
        public IReadOnlyList<CalendarPickerNode> Children { get; } = children;
    }

    /// <summary>
    /// The <see cref="PickAsync{T}"/> sheet with two additions the WS4 pickers need: an initial
    /// selection (the default destination or list) and an optional footer view placed under the
    /// outline. The footer factory receives an action that closes the sheet without a pick.
    /// </summary>
    private Task<T?> PickSheetAsync<T>(string title, string? description, IReadOnlyList<T> roots, Func<T, IEnumerable<T>> children,
        Func<T, string> text, Func<T, NSImage?> symbol, Func<T, NSColor?> tint, Func<T, bool> selectable, string confirmTitle,
        Func<T, string?>? invalid = null, T? initial = null, Func<Action, NSView>? footer = null) where T : class => PresentAsync(window =>
    {
        var completion = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sheet = new NSWindow(new CGRect(0, 0, 400, 460), NSWindowStyle.Titled | NSWindowStyle.Resizable, NSBackingStore.Buffered, false);
        sheet.ReleaseWhenClosed(false);
        sheet.Title = title;

        var source = new PickerSource<T>(roots, children, text, symbol, tint, selectable);
        var outline = new NSOutlineView { HeaderView = null, RowHeight = 26, Style = NSTableViewStyle.SourceList, IndentationPerLevel = 14 };
        var column = new NSTableColumn("item") { ResizingMask = NSTableColumnResizing.Autoresizing };
        outline.AddColumn(column);
        outline.OutlineTableColumn = column;
        outline.DataSource = source;
        outline.Delegate = source;
        WinoAccessibility.Label(outline, title);
        var scroll = new NSScrollView { DocumentView = outline, HasVerticalScroller = true, BorderType = NSBorderType.BezelBorder, TranslatesAutoresizingMaskIntoConstraints = false };

        var heading = WinoStyle.Label(title, WinoStyle.Heading);
        var filter = new NSSearchField { PlaceholderString = Translator.SearchBarPlaceholder, TranslatesAutoresizingMaskIntoConstraints = false };
        var message = WinoStyle.Label(description, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        message.Hidden = string.IsNullOrEmpty(description);
        var cancel = new NSButton { Title = Translator.Buttons_Cancel, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\u001b" };
        var confirm = new NSButton { Title = confirmTitle, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\r", Enabled = false };

        void Finish(T? result)
        {
            if (!completion.TrySetResult(result)) return;
            window.EndSheet(sheet);
        }

        void UpdateState()
        {
            var item = source.ItemAt(outline, outline.SelectedRow);
            var reason = item is null ? null : invalid?.Invoke(item);
            confirm.Enabled = item is not null && selectable(item);
            message.StringValue = reason ?? description ?? string.Empty;
            message.TextColor = reason is null ? WinoStyle.SecondaryText : WinoStyle.Critical;
            message.Hidden = string.IsNullOrEmpty(message.StringValue);
        }

        source.SelectionChanged = UpdateState;
        filter.Changed += (_, _) => { source.Filter = filter.StringValue; outline.ReloadData(); if (string.IsNullOrEmpty(source.Filter)) outline.ExpandItem(null, true); UpdateState(); };
        outline.DoubleClick += (_, _) => { var item = source.ItemAt(outline, outline.ClickedRow); if (item is not null && selectable(item)) Finish(item); };
        cancel.Activated += (_, _) => Finish(null);
        confirm.Activated += (_, _) => { var item = source.ItemAt(outline, outline.SelectedRow); if (item is not null && selectable(item)) Finish(item); };

        var views = new List<NSView> { heading, filter, scroll, message };
        var extra = footer?.Invoke(() => Finish(null));
        if (extra is not null) views.Add(extra);
        var buttons = WinoLayout.HStack(WinoStyle.Space2, WinoLayout.Spacer(), cancel, confirm);
        views.Add(buttons);
        var stack = WinoLayout.VStack(WinoStyle.Space3, views.ToArray());
        stack.EdgeInsets = new NSEdgeInsets(18, 20, 16, 20);
        foreach (var view in new NSView[] { filter, scroll, buttons, message }) view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -40).Active = true;
        scroll.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);
        WinoLayout.Fill(stack, sheet.ContentView!);
        sheet.InitialFirstResponder = filter;

        outline.ReloadData();
        outline.ExpandItem(null, true);
        if (initial is not null)
        {
            for (nint row = 0; row < outline.RowCount; row++)
            {
                if (!ReferenceEquals(source.ItemAt(outline, row), initial)) continue;
                outline.SelectRow(row, false);
                outline.ScrollRowToVisible(row);
                break;
            }
        }
        UpdateState();

        NSObject? closing = null;
        closing = NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.WillCloseNotification, _ => Finish(null), window);
        window.BeginSheet(sheet, _ =>
        {
            if (closing is not null) { NSNotificationCenter.DefaultCenter.RemoveObserver(closing); closing.Dispose(); closing = null; }
            completion.TrySetResult(null);
            outline.DataSource = null;
            outline.Delegate = null;
            source.Dispose();
            sheet.Dispose();
        });
        return completion.Task;
    });
}
