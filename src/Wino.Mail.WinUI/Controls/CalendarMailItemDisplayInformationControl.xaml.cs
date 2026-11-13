using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Helpers;
using Wino.Mail.WinUI;
using Wino.Services;

namespace Wino.Controls;

public sealed partial class CalendarMailItemDisplayInformationControl : UserControl
{
    private static readonly ConcurrentDictionary<(Guid FileId, string CultureName, TimeFormatPreference TimeFormatPreference), string> EventDateRangeCache = [];

    private readonly IMimeFileService _mimeFileService;
    private readonly IPreferencesService _preferencesService;
    private CancellationTokenSource? _loadingCts;
    private bool _isLoaded;

    [GeneratedDependencyProperty]
    public partial MailItemViewModel? MailItem { get; set; }

    [GeneratedDependencyProperty(DefaultValue = MailListDisplayMode.Spacious)]
    public partial MailListDisplayMode DisplayMode { get; set; }

    [GeneratedDependencyProperty(DefaultValue = Wino.Core.Domain.Enums.TimeFormatPreference.UseLanguageCulture)]
    public partial TimeFormatPreference TimeFormatPreference { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string EventDateRangeText { get; set; }

    public CalendarMailItemDisplayInformationControl()
    {
        InitializeComponent();

        _mimeFileService = App.Current.Services.GetRequiredService<IMimeFileService>();
        _preferencesService = App.Current.Services.GetRequiredService<IPreferencesService>();

        DisplayMode = _preferencesService.MailItemDisplayMode;
        TimeFormatPreference = _preferencesService.MailTimeFormatPreference;
    }

    partial void OnMailItemPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MailItemViewModel oldItem)
            oldItem.PropertyChanged -= OnMailItemStatePropertyChanged;

        // Only a loaded row listens: the item outlives recycled rows, and its event would keep them alive.
        if (_isLoaded && e.NewValue is MailItemViewModel newItem)
            newItem.PropertyChanged += OnMailItemStatePropertyChanged;

        _ = LoadEventDateRangeAsync();
    }

    private void OnMailItemStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MailItemViewModel.IsCalendarEvent) && string.IsNullOrEmpty(EventDateRangeText))
            _ = LoadEventDateRangeAsync();
    }

    private void OnControlLoaded(object sender, RoutedEventArgs e)
    {
        if (_isLoaded)
            return;

        _isLoaded = true;
        if (MailItem != null)
            MailItem.PropertyChanged += OnMailItemStatePropertyChanged;
    }

    private void OnControlUnloaded(object sender, RoutedEventArgs e)
    {
        _loadingCts?.Cancel();

        // A recycled row can be loaded into its new place before the old place unloads it.
        if (!_isLoaded || IsLoaded)
            return;

        _isLoaded = false;
        if (MailItem != null)
            MailItem.PropertyChanged -= OnMailItemStatePropertyChanged;
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

        var culture = EventDateRangeFormatter.DisplayCulture;
        var cacheKey = (MailItem.MailCopy.FileId, culture.Name, TimeFormatPreference);
        if (EventDateRangeCache.TryGetValue(cacheKey, out var cachedValue))
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
                EventDateRangeText = string.Empty;
                return;
            }

            var mimeInfo = await _mimeFileService.GetMimeMessageInformationAsync(MailItem.MailCopy.FileId, accountId.Value, token);
            if (mimeInfo == null)
            {
                EventDateRangeText = Translator.UnknownDateHeader;
                return;
            }

            var invitation = MailInvitationReader.Parse(mimeInfo.MimeMessage);
            var renderedDateRange = invitation == null ? string.Empty : EventDateRangeFormatter.Format(invitation, TimeFormatPreference, culture);

            EventDateRangeText = string.IsNullOrWhiteSpace(renderedDateRange) ? Translator.UnknownDateHeader : renderedDateRange;
            EventDateRangeCache.TryAdd(cacheKey, EventDateRangeText);
        }
        catch (OperationCanceledException)
        {
            // Ignore; newer bind request superseded this one.
        }
        catch (Exception)
        {
            EventDateRangeText = Translator.UnknownDateHeader;
        }
    }
}
