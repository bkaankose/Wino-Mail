using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MimeKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Helpers;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI;

namespace Wino.Controls;

public sealed partial class CalendarMailItemDisplayInformationControl : UserControl
{
    private static readonly ConcurrentDictionary<Guid, string> EventDateRangeCache = [];

    private readonly IMimeFileService _mimeFileService;
    private readonly IPreferencesService _preferencesService;
    private CancellationTokenSource? _loadingCts;

    [GeneratedDependencyProperty]
    public partial MailItemViewModel? MailItem { get; set; }

    [GeneratedDependencyProperty(DefaultValue = MailListDisplayMode.Spacious)]
    public partial MailListDisplayMode DisplayMode { get; set; }

    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool Prefer24HourTimeFormat { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string EventDateRangeText { get; set; }

    public event EventHandler<MailOperationPreperationRequest>? HoverActionExecuted;

    public CalendarMailItemDisplayInformationControl()
    {
        InitializeComponent();

        _mimeFileService = App.Current.Services.GetRequiredService<IMimeFileService>();
        _preferencesService = App.Current.Services.GetRequiredService<IPreferencesService>();

        DisplayMode = _preferencesService.MailItemDisplayMode;
        Prefer24HourTimeFormat = _preferencesService.Prefer24HourTimeFormat;

        Unloaded += OnControlUnloaded;
    }

    partial void OnMailItemPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        _ = LoadEventDateRangeAsync();
    }

    private async Task LoadEventDateRangeAsync()
    {
        _loadingCts?.Cancel();
        _loadingCts?.Dispose();
        _loadingCts = new CancellationTokenSource();
        var token = _loadingCts.Token;

        if (MailItem?.MailCopy == null)
        {
            EventDateRangeText = string.Empty;
            return;
        }

        if (EventDateRangeCache.TryGetValue(MailItem.MailCopy.FileId, out var cachedValue))
        {
            EventDateRangeText = cachedValue;
            return;
        }

        try
        {
            var accountId = MailItem.MailCopy.AssignedAccount?.Id;
            if (accountId == null || accountId == Guid.Empty)
            {
                EventDateRangeText = Translator.UnknownDateHeader;
                return;
            }

            var isMimeExists = await _mimeFileService.IsMimeExistAsync(accountId.Value, MailItem.MailCopy.FileId);
            if (!isMimeExists)
            {
                EventDateRangeText = Translator.UnknownDateHeader;
                return;
            }

            var mimeInfo = await _mimeFileService.GetMimeMessageInformationAsync(MailItem.MailCopy.FileId, accountId.Value, token);
            if (mimeInfo == null)
            {
                EventDateRangeText = Translator.UnknownDateHeader;
                return;
            }

            var renderedDateRange = ExtractCalendarDateRange(mimeInfo.MimeMessage);

            EventDateRangeText = string.IsNullOrWhiteSpace(renderedDateRange) ? Translator.UnknownDateHeader : renderedDateRange;
            EventDateRangeCache.TryAdd(MailItem.MailCopy.FileId, EventDateRangeText);
        }
        catch (OperationCanceledException)
        {
            // Ignore; newer bind request superseded this one.
        }
        catch
        {
            EventDateRangeText = Translator.UnknownDateHeader;
        }
    }

    private void BaseMailControlHoverActionExecuted(object sender, MailOperationPreperationRequest e)
        => HoverActionExecuted?.Invoke(this, e);

    private string ExtractCalendarDateRange(MimeMessage message)
    {
        var calendarContent = GetCalendarContent(message);
        if (string.IsNullOrWhiteSpace(calendarContent))
        {
            return string.Empty;
        }

        var unfoldedIcs = UnfoldIcs(calendarContent);
        var eventSection = ExtractFirstVEventSection(unfoldedIcs);

        if (!TryReadIcsDateValue(eventSection, "DTSTART", out var dtStartValue, out var dtStartTzId))
        {
            return string.Empty;
        }

        if (!TryParseIcsDateTime(dtStartValue, dtStartTzId, out var startLocal, out var isAllDay))
        {
            return string.Empty;
        }

        DateTime endLocal = startLocal;
        if (TryReadIcsDateValue(eventSection, "DTEND", out var dtEndValue, out var dtEndTzId))
        {
            if (!TryParseIcsDateTime(dtEndValue, dtEndTzId, out endLocal, out var endIsAllDay))
            {
                endLocal = startLocal;
            }

            isAllDay = isAllDay && endIsAllDay;
        }

        return FormatDisplayDateRange(startLocal, endLocal, isAllDay);
    }

    private static string GetCalendarContent(MimeMessage message)
    {
        var calendarTextPart = message.BodyParts
            .OfType<TextPart>()
            .FirstOrDefault(x => x.ContentType?.MimeType?.Equals("text/calendar", StringComparison.OrdinalIgnoreCase) == true);

        if (calendarTextPart != null)
        {
            return calendarTextPart.Text ?? string.Empty;
        }

        var calendarMimePart = message.BodyParts
            .OfType<MimePart>()
            .FirstOrDefault(x => x.ContentType?.MimeType?.Equals("text/calendar", StringComparison.OrdinalIgnoreCase) == true);

        if (calendarMimePart == null)
        {
            return string.Empty;
        }

        using var stream = new MemoryStream();
        calendarMimePart.Content.DecodeTo(stream);
        var contentBytes = stream.ToArray();

        if (contentBytes.Length == 0)
        {
            return string.Empty;
        }

        var charset = calendarMimePart.ContentType?.Charset;
        var encoding = string.IsNullOrWhiteSpace(charset) ? System.Text.Encoding.UTF8 : System.Text.Encoding.GetEncoding(charset);
        return encoding.GetString(contentBytes);
    }

    private static string UnfoldIcs(string content)
        => content
            .Replace("\r\n ", string.Empty, StringComparison.Ordinal)
            .Replace("\r\n\t", string.Empty, StringComparison.Ordinal)
            .Replace("\n ", string.Empty, StringComparison.Ordinal)
            .Replace("\n\t", string.Empty, StringComparison.Ordinal);

    private static string ExtractFirstVEventSection(string ics)
    {
        if (string.IsNullOrWhiteSpace(ics))
        {
            return string.Empty;
        }

        const string beginVevent = "BEGIN:VEVENT";
        const string endVevent = "END:VEVENT";

        var beginIndex = ics.IndexOf(beginVevent, StringComparison.OrdinalIgnoreCase);
        if (beginIndex < 0)
        {
            return ics;
        }

        var endIndex = ics.IndexOf(endVevent, beginIndex, StringComparison.OrdinalIgnoreCase);
        if (endIndex < 0)
        {
            return ics[beginIndex..];
        }

        return ics.Substring(beginIndex, endIndex - beginIndex + endVevent.Length);
    }

    private static bool TryReadIcsDateValue(string ics, string propertyName, out string value, out string timeZoneId)
    {
        value = string.Empty;
        timeZoneId = string.Empty;

        var lines = ics.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0 || colonIndex == line.Length - 1)
            {
                continue;
            }

            var parameterSection = line[..colonIndex];
            value = line[(colonIndex + 1)..].Trim();

            var paramsSplit = parameterSection.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var parameter in paramsSplit.Skip(1))
            {
                var eqIndex = parameter.IndexOf('=');
                if (eqIndex <= 0 || eqIndex == parameter.Length - 1)
                {
                    continue;
                }

                var name = parameter[..eqIndex].Trim();
                if (!name.Equals("TZID", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                timeZoneId = parameter[(eqIndex + 1)..].Trim().Trim('"');
                break;
            }

            return true;
        }

        return false;
    }

    private static bool TryParseIcsDateTime(string rawValue, string timeZoneId, out DateTime localDateTime, out bool isAllDay)
    {
        localDateTime = default;
        isAllDay = false;

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        var value = rawValue.Trim();

        if (value.Length == 8 && DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnly))
        {
            localDateTime = dateOnly.Date;
            isAllDay = true;
            return true;
        }

        var isUtc = value.EndsWith("Z", StringComparison.OrdinalIgnoreCase);
        if (isUtc)
        {
            value = value[..^1];
        }

        var formats = new[] { "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm" };
        if (!DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
        {
            return false;
        }

        if (isUtc)
        {
            localDateTime = DateTime.SpecifyKind(parsedDate, DateTimeKind.Utc).ToLocalTime();
            return true;
        }

        if (!string.IsNullOrWhiteSpace(timeZoneId) && TryFindTimeZone(timeZoneId, out var sourceZone))
        {
            var unspecifiedTime = DateTime.SpecifyKind(parsedDate, DateTimeKind.Unspecified);
            localDateTime = TimeZoneInfo.ConvertTime(unspecifiedTime, sourceZone, TimeZoneInfo.Local);
            return true;
        }

        localDateTime = DateTime.SpecifyKind(parsedDate, DateTimeKind.Local);
        return true;
    }

    private static bool TryFindTimeZone(string timeZoneId, out TimeZoneInfo timeZone)
    {
        timeZone = null!;

        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out var windowsId))
            {
                try
                {
                    timeZone = TimeZoneInfo.FindSystemTimeZoneById(windowsId);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        return false;
    }

    private string FormatDisplayDateRange(DateTime startLocal, DateTime endLocal, bool isAllDay)
    {
        if (endLocal < startLocal)
        {
            endLocal = startLocal;
        }

        var culture = CultureInfo.DefaultThreadCurrentUICulture;
        if (isAllDay)
        {
            var adjustedEnd = endLocal.Date > startLocal.Date ? endLocal.Date.AddDays(-1) : startLocal.Date;

            if (adjustedEnd.Date > startLocal.Date)
            {
                return $"{startLocal.ToString("d", culture)} - {adjustedEnd.ToString("d", culture)} ({Translator.CalendarItemAllDay})";
            }

            return $"{startLocal.ToString("d", culture)} ({Translator.CalendarItemAllDay})";
        }

        var timeFormat = Prefer24HourTimeFormat ? "HH:mm" : "h:mm tt";

        if (startLocal.Date == endLocal.Date)
        {
            return $"{startLocal.ToString($"ddd, MMM d {timeFormat}", culture)} - {endLocal.ToString(timeFormat, culture)}";
        }

        return $"{startLocal.ToString($"ddd, MMM d {timeFormat}", culture)} - {endLocal.ToString($"ddd, MMM d {timeFormat}", culture)}";
    }

    private void OnControlUnloaded(object sender, RoutedEventArgs e)
    {
        _loadingCts?.Cancel();
    }
}
