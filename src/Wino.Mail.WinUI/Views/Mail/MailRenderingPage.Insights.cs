using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Serilog;
using Windows.ApplicationModel.DataTransfer;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Reader;
using Wino.Mail.Controls.Core.ContextFlyout;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.ActionCards;
using Wino.Mail.WinUI.Rendering;
using Wino.Renderer;
using Wino.Renderer.Policy;
using Wino.Renderer.Primitives;
using Wino.Renderer.Selection;
using WinRT;

namespace Wino.Mail.WinUI.Views.Mail;

public sealed partial class MailRenderingPage
{
    private void MessageReader_InsightsChanged(object? sender, MailReaderInsights insights)
        => ViewModel.ShowInsights(insights);

    private void ActionCard_ExpansionChanged(object? sender, bool isExpanded)
        => ViewModel.AreActionCardsExpanded = isExpanded;

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private void ActionCardAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ActionCardAction action)
            ViewModel.InvokeActionCardActionCommand.Execute(action);
    }

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private void ActionCardCopy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string value && value.Length > 0)
            ViewModel.CopyClipboardCommand.Execute(value);
    }

    // A tap on an underlined date opens its menu; a date inside a link stays a link.
    private void MailView_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_mailView.Reader is not { } reader)
            return;

        var position = e.GetPosition(_mailView);
        var point = new PxPoint((float)position.X, (float)position.Y);

        if (reader.HitTest(point).Link is not null || _messageReader.GetDateAt(position.X, position.Y) is not { } date)
            return;

        e.Handled = true;
        EngineContextMenu.Show(_mailView, position, CreateDateEntries(date), MailRendererHost.ActualTheme);
    }

    private List<ContextFlyoutMenuEntry> CreateDateEntries(MailDetectedDate date)
    {
        var text = MailRenderingPageViewModel.FormatDetectedDate(date);

        return
        [
            new ContextFlyoutCommandEntry
            {
                Text = text,
                IsEnabled = false,
                AutomationId = "MailRenderingDateHeader"
            },
            new ContextFlyoutCommandEntry
            {
                Text = Translator.Reader_Date_AddToCalendar,
                Icon = new ContextFlyoutIcon(WinoIconGlyphs.GetGlyph(WinoIconGlyph.CalendarAdd)),
                Command = ViewModel.AddDateToCalendarCommand,
                CommandParameter = date,
                AutomationId = "MailRenderingDateAddToCalendar"
            },
            new ContextFlyoutCommandEntry
            {
                Text = Translator.Reader_Date_CreateTask,
                Icon = new ContextFlyoutIcon(WinoIconGlyphs.GetGlyph(WinoIconGlyph.TaskList)),
                Command = ViewModel.CreateTaskForDateCommand,
                CommandParameter = date,
                AutomationId = "MailRenderingDateCreateTask"
            },
            ContextFlyoutSeparatorEntry.Instance,
            new ContextFlyoutCommandEntry
            {
                Text = Translator.Reader_Date_Copy,
                Icon = new ContextFlyoutIcon(WinoIconGlyphs.GetGlyph(WinoIconGlyph.Copy)),
                Command = ViewModel.CopyClipboardCommand,
                CommandParameter = text,
                AutomationId = "MailRenderingDateCopy"
            }
        ];
    }

    private void AddTargetEntries(ContextMenuRequest request, List<ContextFlyoutMenuEntry> entries)
    {
        foreach (var target in request.Targets)
        {
            if (target.Kind == ContextTargetKind.Date && _messageReader.GetDate(target.Index) is { } date)
            {
                entries.AddRange(CreateDateEntries(date));
                entries.Add(ContextFlyoutSeparatorEntry.Instance);
                return;
            }
        }
    }

    private void AddTableEntries(ContextMenuRequest request, List<ContextFlyoutMenuEntry> entries)
    {
        foreach (var target in request.Targets)
        {
            if (target.Kind != ContextTargetKind.Table)
                continue;

            entries.Add(new ContextFlyoutCommandEntry
            {
                Text = Translator.Reader_ContextMenu_CopyTable,
                Icon = new ContextFlyoutIcon(WinoIconGlyphs.GetGlyph(WinoIconGlyph.Table)),
                Command = new RelayCommand(() => CopyTable(target, TableTextFormat.TabSeparated)),
                AutomationId = "MailRenderingCopyTable"
            });
            entries.Add(new ContextFlyoutCommandEntry
            {
                Text = Translator.Reader_ContextMenu_CopyTableAsCsv,
                Icon = new ContextFlyoutIcon(WinoIconGlyphs.GetGlyph(WinoIconGlyph.Document)),
                Command = new RelayCommand(() => CopyTable(target, TableTextFormat.Csv)),
                AutomationId = "MailRenderingCopyTableAsCsv"
            });
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

            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(content.PlainText);

            if (format == TableTextFormat.TabSeparated && !string.IsNullOrEmpty(content.Html))
                package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(content.Html));

            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to copy a table from the mail renderer.");
        }
    }

    private static string GetLinkMismatchText(LinkTextMismatch? mismatch)
        => mismatch is null
            ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, Translator.Reader_LinkTextMismatchFormat, mismatch.TextHost, mismatch.TargetHost);
}
