#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.Api.Contracts.Auth;

namespace Wino.Core.ViewModels;

public partial class WinoAccountManagementPageViewModel : CoreBaseViewModel
{
    private const string BuyAiPackUrl = "https://example.com/wino-ai-pack";

    private readonly IWinoAccountProfileService _profileService;
    private readonly IWinoAccountApiClient _apiClient;
    private readonly IPreferencesService _preferencesService;
    private readonly IMailDialogService _dialogService;
    private readonly INativeAppService _nativeAppService;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedOut))]
    [NotifyPropertyChangedFor(nameof(CanShowBuyAiPack))]
    public partial bool IsSignedIn { get; set; }

    [ObservableProperty]
    public partial string AccountEmail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AccountStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowAiUsage))]
    [NotifyPropertyChangedFor(nameof(CanShowBuyAiPack))]
    public partial bool HasAiPack { get; set; }

    [ObservableProperty]
    public partial string AiPackStateText { get; set; } = Translator.WinoAccount_Management_AiPackInactive;

    [ObservableProperty]
    public partial string AiUsageSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AiBillingPeriodSummary { get; set; } = string.Empty;

    public bool IsSignedOut => !IsSignedIn;
    public bool CanShowAiUsage => HasAiPack;
    public bool CanShowBuyAiPack => IsSignedIn && !HasAiPack;

    public WinoAccountManagementPageViewModel(IWinoAccountProfileService profileService,
                                              IWinoAccountApiClient apiClient,
                                              IPreferencesService preferencesService,
                                              IMailDialogService dialogService,
                                              INativeAppService nativeAppService)
    {
        _profileService = profileService;
        _apiClient = apiClient;
        _preferencesService = preferencesService;
        _dialogService = dialogService;
        _nativeAppService = nativeAppService;
    }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        _ = LoadAsync();
    }

    [RelayCommand]
    private async Task RegisterAsync()
    {
        var account = await _dialogService.ShowWinoAccountRegistrationDialogAsync();
        if (account == null)
        {
            return;
        }

        _dialogService.InfoBarMessage(Translator.GeneralTitle_Info,
                                      string.Format(Translator.WinoAccount_RegisterSuccessMessage, account.Email),
                                      InfoBarMessageType.Success);

        await LoadAsync();
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        var account = await _dialogService.ShowWinoAccountLoginDialogAsync();
        if (account == null)
        {
            return;
        }

        _dialogService.InfoBarMessage(Translator.GeneralTitle_Info,
                                      string.Format(Translator.WinoAccount_LoginSuccessMessage, account.Email),
                                      InfoBarMessageType.Success);

        await LoadAsync();
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        var account = await _profileService.GetActiveAccountAsync().ConfigureAwait(false);
        if (account == null)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Warning,
                                          Translator.WinoAccount_SignOut_NoAccountMessage,
                                          InfoBarMessageType.Warning);
            return;
        }

        await _profileService.SignOutAsync().ConfigureAwait(false);

        _dialogService.InfoBarMessage(Translator.GeneralTitle_Info,
                                      string.Format(Translator.WinoAccount_SignOut_SuccessMessage, account.Email),
                                      InfoBarMessageType.Success);

        await ResetSignedOutStateAsync();
    }

    [RelayCommand]
    private async Task OpenBuyPageAsync() => await _nativeAppService.LaunchUriAsync(new Uri(BuyAiPackUrl));

    [RelayCommand]
    private async Task ExportSettingsAsync()
    {
        string settingsJson;

        try
        {
            if (await EnsureAuthenticatedAccountAsync().ConfigureAwait(false) == null)
            {
                return;
            }

            var settings = CollectPreferencesSnapshot();
            if (settings.Count == 0)
            {
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Warning,
                                              Translator.WinoAccount_Management_EmptyExport,
                                              InfoBarMessageType.Warning);
                return;
            }

            settingsJson = SerializePreferencesSnapshot(settings);

            if (string.IsNullOrWhiteSpace(settingsJson) || settingsJson == "{}")
            {
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Warning,
                                              Translator.WinoAccount_Management_EmptyExport,
                                              InfoBarMessageType.Warning);
                return;
            }
        }
        catch (Exception)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error,
                                          Translator.WinoAccount_Management_SerializeFailed,
                                          InfoBarMessageType.Error);
            return;
        }

        try
        {
            if (await EnsureAuthenticatedAccountAsync().ConfigureAwait(false) == null)
            {
                return;
            }

            var isSaved = await _apiClient.SaveSettingsAsync(settingsJson).ConfigureAwait(false);

            if (!isSaved)
            {
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error,
                                              Translator.WinoAccount_Management_ActionFailed,
                                              InfoBarMessageType.Error);
                return;
            }

            _dialogService.InfoBarMessage(Translator.GeneralTitle_Info,
                                          Translator.WinoAccount_Management_ExportSucceeded,
                                          InfoBarMessageType.Success);
        }
        catch (Exception)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error,
                                          Translator.WinoAccount_Management_ActionFailed,
                                          InfoBarMessageType.Error);
        }
    }

    [RelayCommand]
    private async Task ImportSettingsAsync()
    {
        try
        {
            if (await EnsureAuthenticatedAccountAsync().ConfigureAwait(false) == null)
            {
                return;
            }

            var settingsJson = await _apiClient.GetSettingsAsync().ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(settingsJson))
            {
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Warning,
                                              Translator.WinoAccount_Management_NoRemoteSettings,
                                              InfoBarMessageType.Warning);
                return;
            }

            using var document = JsonDocument.Parse(settingsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.EnumerateObject().Any())
            {
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Warning,
                                              Translator.WinoAccount_Management_ImportEmpty,
                                              InfoBarMessageType.Warning);
                return;
            }

            var (appliedCount, failedCount) = ApplyPreferencesSnapshot(document.RootElement);

            if (appliedCount == 0)
            {
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Warning,
                                              Translator.WinoAccount_Management_ImportEmpty,
                                              InfoBarMessageType.Warning);
                return;
            }

            if (failedCount > 0)
            {
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Warning,
                                              string.Format(Translator.WinoAccount_Management_ImportPartial, appliedCount, failedCount),
                                              InfoBarMessageType.Warning);
                return;
            }

            _dialogService.InfoBarMessage(Translator.GeneralTitle_Info,
                                          string.Format(Translator.WinoAccount_Management_ImportSucceeded, appliedCount),
                                          InfoBarMessageType.Success);
        }
        catch (Exception)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error,
                                          Translator.WinoAccount_Management_ActionFailed,
                                          InfoBarMessageType.Error);
        }
    }

    private async Task LoadAsync()
    {
        await ExecuteUIThread(() => IsBusy = true);

        try
        {
            var account = await EnsureAuthenticatedAccountAsync().ConfigureAwait(false);

            if (account == null)
            {
                await ResetSignedOutStateAsync();
                return;
            }

            var currentUserResponse = await _apiClient.GetCurrentUserAsync().ConfigureAwait(false);
            var aiStatusResponse = await _apiClient.GetAiStatusAsync().ConfigureAwait(false);

            var resolvedUser = currentUserResponse.IsSuccess && currentUserResponse.Result != null
                ? currentUserResponse.Result
                : new AuthUserDto(account.Id, account.Email, account.AccountStatus, account.HasPassword, account.HasGoogleLogin, account.HasFacebookLogin);

            await ExecuteUIThread(() =>
            {
                IsSignedIn = true;
                AccountEmail = resolvedUser.Email;
                AccountStatusText = string.Format(Translator.WinoAccount_Management_StatusLabel, resolvedUser.AccountStatus);
            });

            UpdateAiPackState(aiStatusResponse.IsSuccess ? aiStatusResponse.Result : null);

            if (!currentUserResponse.IsSuccess || !aiStatusResponse.IsSuccess)
            {
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Warning,
                                              Translator.WinoAccount_Management_LoadFailed,
                                              InfoBarMessageType.Warning);
            }
        }
        catch (Exception)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error,
                                          Translator.WinoAccount_Management_LoadFailed,
                                          InfoBarMessageType.Error);
            await ResetSignedOutStateAsync();
        }
        finally
        {
            await ExecuteUIThread(() => IsBusy = false);
        }
    }

    private async Task ResetSignedOutStateAsync()
    {
        await ExecuteUIThread(() =>
        {
            IsSignedIn = false;
            AccountEmail = string.Empty;
            AccountStatusText = string.Empty;
            HasAiPack = false;
            AiPackStateText = Translator.WinoAccount_Management_AiPackInactive;
            AiUsageSummary = string.Empty;
            AiBillingPeriodSummary = string.Empty;
        });
    }

    private async Task<WinoAccount?> EnsureAuthenticatedAccountAsync()
    {
        var account = await _profileService.GetActiveAccountAsync().ConfigureAwait(false);
        if (account == null)
        {
            return null;
        }

        if (account.AccessTokenExpiresAtUtc <= DateTime.UtcNow.AddMinutes(1))
        {
            var refreshResult = await _profileService.RefreshAsync().ConfigureAwait(false);

            if (!refreshResult.IsSuccess)
            {
                return null;
            }

            account = refreshResult.Account ?? await _profileService.GetActiveAccountAsync().ConfigureAwait(false);
        }

        return account != null && !string.IsNullOrWhiteSpace(account.AccessToken)
            ? account
            : null;
    }

    private void UpdateAiPackState(AiStatusResultDto? aiStatus)
    {
        var hasAiPack = aiStatus?.HasAiPack == true;
        var usageText = Translator.WinoAccount_Management_AiPackUnknownUsage;
        var billingText = string.Empty;

        if (hasAiPack && aiStatus?.Used is int used && aiStatus.MonthlyLimit is int limit && aiStatus.Remaining is int remaining)
        {
            usageText = string.Format(Translator.WinoAccount_Management_AiPackUsage, used, limit, remaining);
        }

        if (hasAiPack && aiStatus?.CurrentPeriodStartUtc is DateTimeOffset periodStart && aiStatus.CurrentPeriodEndUtc is DateTimeOffset periodEnd)
        {
            billingText = string.Format(Translator.WinoAccount_Management_AiPackBillingPeriod, periodStart.LocalDateTime, periodEnd.LocalDateTime);
        }

        _ = ExecuteUIThread(() =>
        {
            HasAiPack = hasAiPack;
            AiPackStateText = hasAiPack
                ? Translator.WinoAccount_Management_AiPackActive
                : Translator.WinoAccount_Management_AiPackInactive;
            AiUsageSummary = hasAiPack ? usageText : string.Empty;
            AiBillingPeriodSummary = hasAiPack ? billingText : string.Empty;
        });
    }

    private Dictionary<string, object?> CollectPreferencesSnapshot()
    {
        var settings = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var property in GetSyncablePreferenceProperties())
        {
            settings[property.Name] = property.GetValue(_preferencesService);
        }

        return settings;
    }

    private static string SerializePreferencesSnapshot(Dictionary<string, object?> settings)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            foreach (var setting in settings)
            {
                WritePreferenceValue(writer, setting.Key, setting.Value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private (int appliedCount, int failedCount) ApplyPreferencesSnapshot(JsonElement rootElement)
    {
        var appliedCount = 0;
        var failedCount = 0;

        foreach (var property in GetSyncablePreferenceProperties())
        {
            if (!rootElement.TryGetProperty(property.Name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            try
            {
                property.SetValue(_preferencesService, ReadPreferenceValue(property.PropertyType, value));
                appliedCount++;
            }
            catch (Exception)
            {
                failedCount++;
            }
        }

        return (appliedCount, failedCount);
    }

    private static void WritePreferenceValue(Utf8JsonWriter writer, string propertyName, object? value)
    {
        if (value == null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        switch (value)
        {
            case string stringValue:
                writer.WriteString(propertyName, stringValue);
                return;
            case bool boolValue:
                writer.WriteBoolean(propertyName, boolValue);
                return;
            case int intValue:
                writer.WriteNumber(propertyName, intValue);
                return;
            case long longValue:
                writer.WriteNumber(propertyName, longValue);
                return;
            case double doubleValue:
                writer.WriteNumber(propertyName, doubleValue);
                return;
            case float floatValue:
                writer.WriteNumber(propertyName, floatValue);
                return;
            case Guid guidValue:
                writer.WriteString(propertyName, guidValue);
                return;
            case TimeSpan timeSpanValue:
                writer.WriteString(propertyName, timeSpanValue.ToString("c", CultureInfo.InvariantCulture));
                return;
        }

        var valueType = Nullable.GetUnderlyingType(value.GetType()) ?? value.GetType();
        if (valueType.IsEnum)
        {
            writer.WriteString(propertyName, value.ToString());
            return;
        }

        writer.WriteString(propertyName, Convert.ToString(value, CultureInfo.InvariantCulture));
    }

    private static object? ReadPreferenceValue(Type propertyType, JsonElement value)
    {
        var targetType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (targetType == typeof(string))
        {
            return value.GetString() ?? string.Empty;
        }

        if (targetType == typeof(bool))
        {
            return value.GetBoolean();
        }

        if (targetType == typeof(int))
        {
            return value.GetInt32();
        }

        if (targetType == typeof(long))
        {
            return value.GetInt64();
        }

        if (targetType == typeof(double))
        {
            return value.GetDouble();
        }

        if (targetType == typeof(float))
        {
            return value.GetSingle();
        }

        if (targetType == typeof(Guid))
        {
            return Guid.Parse(value.GetString() ?? string.Empty);
        }

        if (targetType == typeof(TimeSpan))
        {
            return TimeSpan.Parse(value.GetString() ?? string.Empty, CultureInfo.InvariantCulture);
        }

        if (targetType.IsEnum)
        {
            return Enum.Parse(targetType, value.GetString() ?? string.Empty, true);
        }

        return Convert.ChangeType(value.GetString(), targetType, CultureInfo.InvariantCulture);
    }

    private static IEnumerable<PropertyInfo> GetSyncablePreferenceProperties()
    {
        foreach (var property in typeof(IPreferencesService).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            yield return property;
        }
    }
}
