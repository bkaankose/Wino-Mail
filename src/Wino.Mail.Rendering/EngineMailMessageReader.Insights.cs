using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Reader;
using Wino.Renderer;
using Wino.Renderer.Extraction;
using Wino.Renderer.Primitives;

namespace Wino.Mail.Rendering;

public sealed partial class EngineMailMessageReader
{
    private const string FallbackDateLanguage = "en-US";

    private MailReaderSession? _watchedReader;
    private MailInsights? _reportedInsights;

    /// <summary>
    /// Raised on the UI thread when the reader has analysed the shown message: its structured data and the dates in
    /// its text. Raised with <see cref="MailReaderInsights.Empty"/> when a message without analysis is shown.
    /// </summary>
    public event EventHandler<MailReaderInsights>? InsightsChanged;

    /// <summary>
    /// The device's regional format, then English: dates are written the way the user's region writes them, and English
    /// covers mail from services that only write English.
    /// </summary>
    private static IReadOnlyList<string> DateLanguages { get; } = CreateDateLanguages();

    private static string[] CreateDateLanguages()
    {
        var regional = DisplayCulture.Regional.Name;

        return string.IsNullOrEmpty(regional) || string.Equals(regional, FallbackDateLanguage, StringComparison.OrdinalIgnoreCase)
            ? [FallbackDateLanguage]
            : [regional, FallbackDateLanguage];
    }

    private static InsightKinds ToInsightKinds(MailRenderingOptions? options)
    {
        var kinds = InsightKinds.None;

        if (options?.FindStructuredData == true)
            kinds |= InsightKinds.StructuredData;

        if (options?.FindDates == true)
            kinds |= InsightKinds.Dates;

        return kinds;
    }

    private void WatchInsights()
    {
        var reader = _view.Reader;
        if (ReferenceEquals(reader, _watchedReader))
            return;

        _watchedReader?.InsightsChanged -= Reader_InsightsChanged;
        _watchedReader = reader;

        if (reader is null)
            return;

        reader.InsightsChanged += Reader_InsightsChanged;

        // The pass may have finished before the session was watched.
        if (reader.Insights is not null)
            Reader_InsightsChanged(reader, EventArgs.Empty);
    }

    private void Reader_InsightsChanged(object? sender, EventArgs e)
    {
        if (sender is not MailReaderSession reader || !ReferenceEquals(reader, _watchedReader))
            return;

        var insights = reader.Insights;
        if (ReferenceEquals(insights, _reportedInsights))
            return;

        _reportedInsights = insights;

        if (insights is null)
            return;

        Log.Debug("Reader insights: {StructuredCount} structured items, {DateCount} dates, limits {Limits}",
            insights.StructuredData.Count, insights.Dates.Count, string.Join(",", insights.LimitsHit));

        if (insights.Dates.Count > 0)
            reader.HighlightRanges(insights.Dates.Select(static date => date.Range).ToArray(), HighlightStyle.Underline);

        InsightsChanged?.Invoke(this, new MailReaderInsights(
            insights.StructuredData.Select(ToDomainItem).ToArray(),
            insights.Dates.Select(ToDomainDate).ToArray()));
    }

    /// <summary>The date whose text is at <paramref name="x"/>, <paramref name="y"/> in view coordinates, if any.</summary>
    public MailDetectedDate? GetDateAt(double x, double y)
    {
        if (_view.Reader is not { Insights: { } insights } reader)
            return null;

        if (reader.HitTest(new PxPoint((float)x, (float)y)).Date is not { } date)
            return null;

        var index = IndexOf(insights.Dates, date);
        return index < 0 ? null : ToDomainDate(date, index);
    }

    /// <summary>The date a context menu target names by its index, while the analysis that found it is still current.</summary>
    public MailDetectedDate? GetDate(int index)
        => _view.Reader is { Insights: { } insights } && index >= 0 && index < insights.Dates.Count
            ? ToDomainDate(insights.Dates[index], index)
            : null;

    private static int IndexOf(IReadOnlyList<DetectedDate> dates, DetectedDate date)
    {
        for (var i = 0; i < dates.Count; i++)
        {
            if (ReferenceEquals(dates[i], date) || dates[i] == date)
                return i;
        }

        return -1;
    }

    private static MailDetectedDate ToDomainDate(DetectedDate date, int index)
        => new(index, date.Date, date.Time, date.HasYear, date.Text);

    private static MailStructuredItem ToDomainItem(StructuredItem item)
        => new(item.Type, item.Properties.Select(static property => new MailStructuredProperty(property.Name, ToDomainValue(property.Value))).ToArray());

    private static MailStructuredValue ToDomainValue(StructuredValue value) => value switch
    {
        StructuredValue.Text text => new MailStructuredText(text.Value),
        StructuredValue.Number number => new MailStructuredNumber(number.Value),
        StructuredValue.Boolean boolean => new MailStructuredBoolean(boolean.Value),
        StructuredValue.Item item => ToDomainItem(item.Value),
        StructuredValue.List list => new MailStructuredList(list.Values.Select(ToDomainValue).ToArray()),
        _ => new MailStructuredText(string.Empty)
    };

    private void ResetInsights()
    {
        _reportedInsights = null;
        InsightsChanged?.Invoke(this, MailReaderInsights.Empty);
    }
}
