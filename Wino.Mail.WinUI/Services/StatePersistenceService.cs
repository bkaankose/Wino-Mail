using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI;

namespace Wino.Services;

public class StatePersistenceService : ObservableObject, IStatePersistanceService
{
    public event EventHandler<string?>? StatePropertyChanged;

    private const string OpenPaneLengthKey = nameof(OpenPaneLengthKey);
    private const string MailListPaneLengthKey = nameof(MailListPaneLengthKey);

    private readonly IConfigurationService _configurationService;

    public StatePersistenceService(IConfigurationService configurationService)
    {
        _configurationService = configurationService;

        _openPaneLength = _configurationService.Get(OpenPaneLengthKey, 320d);
        _mailListPaneLength = _configurationService.Get(MailListPaneLengthKey, 420d);
        _calendarDisplayType = _configurationService.Get(nameof(CalendarDisplayType), CalendarDisplayType.Week);
        _dayDisplayCount = _configurationService.Get(nameof(DayDisplayCount), 1);

        PropertyChanged += ServicePropertyChanged;
    }

    private void ServicePropertyChanged(object? sender, PropertyChangedEventArgs e) => StatePropertyChanged?.Invoke(this, e?.PropertyName ?? string.Empty);

    public bool IsBackButtonVisible =>
        ApplicationMode == WinoApplicationMode.Mail
            ? IsReadingMail && IsReaderNarrowed
            : IsEventDetailsVisible;

    private WinoApplicationMode applicationMode = WinoApplicationMode.Mail;

    public WinoApplicationMode ApplicationMode
    {
        get => applicationMode;
        set
        {
            if (SetProperty(ref applicationMode, value))
            {
                OnPropertyChanged(nameof(IsBackButtonVisible));
            }
        }
    }

    private bool isEventDetailsVisible;

    public bool IsEventDetailsVisible
    {
        get => isEventDetailsVisible;
        set
        {
            if (SetProperty(ref isEventDetailsVisible, value))
            {
                OnPropertyChanged(nameof(IsBackButtonVisible));

                IsReaderNarrowed = value;
                IsReadingMail = value;
            }
        }
    }

    private bool isReadingMail;

    public bool IsReadingMail
    {
        get => isReadingMail;
        set
        {
            if (SetProperty(ref isReadingMail, value))
            {
                OnPropertyChanged(nameof(IsBackButtonVisible));
            }
        }
    }

    private bool shouldShiftMailRenderingDesign;

    public bool ShouldShiftMailRenderingDesign
    {
        get { return shouldShiftMailRenderingDesign; }
        set { shouldShiftMailRenderingDesign = value; }
    }

    private bool isReaderNarrowed;

    public bool IsReaderNarrowed
    {
        get => isReaderNarrowed;
        set
        {
            if (SetProperty(ref isReaderNarrowed, value))
            {
                OnPropertyChanged(nameof(IsBackButtonVisible));
            }
        }
    }

    private string coreWindowTitle = string.Empty;

    public string CoreWindowTitle
    {
        get => coreWindowTitle;
        set
        {
            if (SetProperty(ref coreWindowTitle, value))
            {
                UpdateAppCoreWindowTitle();
            }
        }
    }

    private double _openPaneLength;
    public double OpenPaneLength
    {
        get => _openPaneLength;
        set
        {
            if (SetProperty(ref _openPaneLength, value))
            {
                _configurationService.Set(OpenPaneLengthKey, value);
            }
        }
    }

    private double _mailListPaneLength;
    public double MailListPaneLength
    {
        get => _mailListPaneLength;
        set
        {
            if (SetProperty(ref _mailListPaneLength, value))
            {
                _configurationService.Set(MailListPaneLengthKey, value);
            }
        }
    }

    private CalendarDisplayType _calendarDisplayType;
    public CalendarDisplayType CalendarDisplayType
    {
        get => _calendarDisplayType;
        set
        {
            if (SetProperty(ref _calendarDisplayType, value))
            {
                _configurationService.Set(nameof(CalendarDisplayType), value);
            }
        }
    }

    private int _dayDisplayCount;
    public int DayDisplayCount
    {
        get => _dayDisplayCount;
        set
        {
            if (SetProperty(ref _dayDisplayCount, value))
            {
                _configurationService.Set(nameof(DayDisplayCount), value);
            }
        }
    }

    private void UpdateAppCoreWindowTitle() => WinoApplication.MainWindow.Title = CoreWindowTitle;
}
