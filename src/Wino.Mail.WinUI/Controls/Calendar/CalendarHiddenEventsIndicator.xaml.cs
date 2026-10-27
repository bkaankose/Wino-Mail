using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;

namespace Wino.Calendar.Controls;

public sealed partial class CalendarHiddenEventsIndicator : UserControl
{
    private int _count;
    private DateOnly _date;

    public CalendarHiddenEventsIndicator()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
    }

    public event EventHandler? Invoked;

    public bool IsEarlier { get; private set; }

    public bool HasFocus => IndicatorButton.FocusState != FocusState.Unfocused;

    public void SetDirection(bool isEarlier)
    {
        IsEarlier = isEarlier;
        ChevronIcon.Icon = isEarlier ? WinoIconGlyph.ChevronUp : WinoIconGlyph.ChevronDown;
        AutomationProperties.SetAutomationId(IndicatorButton, isEarlier ? "CalendarEarlierEventsIndicator" : "CalendarLaterEventsIndicator");
    }

    public void ShowCount(int count, DateOnly date, CultureInfo? culture)
    {
        if (count == _count && (count == 0 || date == _date))
        {
            return;
        }

        _count = count;
        _date = date;

        if (count <= 0)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        var displayCulture = culture ?? CultureInfo.CurrentCulture;
        var format = IsEarlier
            ? count == 1 ? Translator.Calendar_EarlierEvents_Singular : Translator.Calendar_EarlierEvents_Plural
            : count == 1 ? Translator.Calendar_LaterEvents_Singular : Translator.Calendar_LaterEvents_Plural;
        var description = string.Format(displayCulture, format, count);

        CountText.Text = count.ToString(displayCulture);
        ToolTipService.SetToolTip(IndicatorButton, description);
        AutomationProperties.SetName(
            IndicatorButton,
            string.Format(
                displayCulture,
                Translator.Calendar_HiddenEvents_AccessibleName,
                description,
                date.ToDateTime(TimeOnly.MinValue).ToString("D", displayCulture)));
        Visibility = Visibility.Visible;
    }

    private void IndicatorButtonClick(object sender, RoutedEventArgs e) => Invoked?.Invoke(this, EventArgs.Empty);
}
