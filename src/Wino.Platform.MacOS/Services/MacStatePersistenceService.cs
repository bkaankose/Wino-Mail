using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.MacOS.Services;

/// <summary>Portable shell state. The head subscribes to title changes and owns its windows.</summary>
public sealed class MacStatePersistenceService : ObservableObject, IStatePersistanceService
{
    private const string OpenPaneLengthKey = nameof(OpenPaneLengthKey);
    private const string MailListPaneLengthKey = nameof(MailListPaneLengthKey);
    private readonly IConfigurationService _configuration;
    private double _openPaneLength;
    private double _mailListPaneLength;
    private CalendarDisplayType _calendarDisplayType;
    private int _dayDisplayCount;
    private string _coreWindowTitle = string.Empty;
    private string _appModeTitle = "Wino Mail";
    private WinoApplicationMode _applicationMode = WinoApplicationMode.Mail;
    private bool _isReadingMail, _isReaderNarrowed, _isEventDetailsVisible, _shouldShiftMailRenderingDesign;

    public MacStatePersistenceService(IConfigurationService configuration)
    {
        _configuration = configuration;
        _openPaneLength = ValidLength(configuration.Get(OpenPaneLengthKey, 340d), 340d);
        _mailListPaneLength = ValidLength(configuration.Get(MailListPaneLengthKey, 420d), 420d);
        _calendarDisplayType = ValidCalendar(configuration.Get(nameof(CalendarDisplayType), CalendarDisplayType.Week));
        _dayDisplayCount = Math.Max(1, configuration.Get(nameof(DayDisplayCount), 1));
    }

    public event EventHandler<string>? StatePropertyChanged;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        StatePropertyChanged?.Invoke(this, e.PropertyName ?? string.Empty);
    }

    public bool IsReadingMail { get => _isReadingMail; set => SetProperty(ref _isReadingMail, value); }
    public bool IsReaderNarrowed { get => _isReaderNarrowed; set => SetProperty(ref _isReaderNarrowed, value); }
    public bool ShouldShiftMailRenderingDesign { get => _shouldShiftMailRenderingDesign; set => SetProperty(ref _shouldShiftMailRenderingDesign, value); }
    public string CoreWindowTitle { get => _coreWindowTitle; set => SetProperty(ref _coreWindowTitle, value ?? string.Empty); }
    public string AppModeTitle
    {
        get => _appModeTitle;
        set { if (SetProperty(ref _appModeTitle, value ?? string.Empty)) _configuration.Set(nameof(AppModeTitle), _appModeTitle); }
    }

    public WinoApplicationMode ApplicationMode
    {
        get => _applicationMode;
        set => SetProperty(ref _applicationMode, Enum.IsDefined(value) ? value : WinoApplicationMode.Mail);
    }

    public bool IsEventDetailsVisible
    {
        get => _isEventDetailsVisible;
        set
        {
            if (SetProperty(ref _isEventDetailsVisible, value))
            {
                IsReaderNarrowed = value;
                IsReadingMail = value;
            }
        }
    }

    public double OpenPaneLength
    {
        get => _openPaneLength;
        set { if (SetProperty(ref _openPaneLength, ValidLength(value, 340d))) _configuration.Set(OpenPaneLengthKey, _openPaneLength); }
    }

    public double MailListPaneLength
    {
        get => _mailListPaneLength;
        set { if (SetProperty(ref _mailListPaneLength, ValidLength(value, 420d))) _configuration.Set(MailListPaneLengthKey, _mailListPaneLength); }
    }

    public CalendarDisplayType CalendarDisplayType
    {
        get => _calendarDisplayType;
        set { if (SetProperty(ref _calendarDisplayType, ValidCalendar(value))) _configuration.Set(nameof(CalendarDisplayType), _calendarDisplayType); }
    }

    public int DayDisplayCount
    {
        get => _dayDisplayCount;
        set { if (SetProperty(ref _dayDisplayCount, Math.Max(1, value))) _configuration.Set(nameof(DayDisplayCount), _dayDisplayCount); }
    }

    private static CalendarDisplayType ValidCalendar(CalendarDisplayType value) => Enum.IsDefined(value) ? value : CalendarDisplayType.Week;
    private static double ValidLength(double value, double fallback) => double.IsFinite(value) && value >= 0 ? value : fallback;
}
