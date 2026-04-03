using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Ai;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.WinUI.Services;

namespace Wino.Mail.WinUI.Controls;

public sealed partial class AiActionsPanel : UserControl, IDisposable
{
    public event EventHandler? CloseRequested;
    private readonly IWinoAccountProfileService _profileService = App.Current.Services.GetRequiredService<IWinoAccountProfileService>();
    private readonly IStoreManagementService _storeManagementService = App.Current.Services.GetRequiredService<IStoreManagementService>();
    private readonly IMailDialogService _dialogService = App.Current.Services.GetRequiredService<IMailDialogService>();
    private readonly IAiActionOptionsService _optionsService = App.Current.Services.GetRequiredService<IAiActionOptionsService>();

    private bool _disposedValue;
    private bool _isRefreshing;
    private bool _isBusy;
    private AiActionType _lastConfigurableAction = AiActionType.Translate;
    private bool _hasCachedSummary;
    private CancellationTokenSource? _actionCancellationTokenSource;
    private IReadOnlyList<AiTranslateLanguageOption> _translateOptions = Array.Empty<AiTranslateLanguageOption>();
    private IReadOnlyList<AiRewriteModeOption> _rewriteOptions = Array.Empty<AiRewriteModeOption>();

    [GeneratedDependencyProperty(DefaultValue = AiActionType.None)]
    public partial AiActionType AvailableActions { get; set; }

    [GeneratedDependencyProperty]
    public partial IAiHtmlActionHost? HtmlHost { get; set; }

    public AiTranslateLanguageOption? SelectedTranslateLanguageOption { get; set; }

    public AiRewriteModeOption? SelectedRewriteModeOption { get; set; }

    public AiActionsPanel()
    {
        InitializeComponent();
    }

    public void CancelPendingOperation()
    {
        _actionCancellationTokenSource?.Cancel();
    }

    public void Dispose()
    {
        if (_disposedValue)
        {
            return;
        }

        _disposedValue = true;
        CancelAndDisposeActionCancellationToken();
    }

    partial void OnAvailableActionsChanged(AiActionType newValue)
    {
        UpdateActionAvailability();
        ApplySelectedAction(SelectDefaultAction());
        _ = RefreshCachedSummaryStateAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadOptions();
        UpdateActionAvailability();
        ApplySelectedAction(SelectDefaultAction());
        _ = RefreshAvailabilityAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CancelPendingOperation();
    }

    private void LoadOptions()
    {
        // Save current selections before replacing ItemsSource (which clears SelectedItem).
        var previousTranslateCode = SelectedTranslateLanguageOption?.Code;
        var previousRewriteMode = SelectedRewriteModeOption?.Mode;

        _translateOptions = _optionsService.GetTranslateLanguageOptions();
        _rewriteOptions = _optionsService.GetRewriteModeOptions();

        TranslateLanguageComboBox.ItemsSource = _translateOptions;
        RewriteModeComboBox.ItemsSource = _rewriteOptions;

        // Restore selection by matching on value, falling back to first item.
        SelectedTranslateLanguageOption = FindOption(_translateOptions, o => o.Code == previousTranslateCode) ?? (_translateOptions.Count > 0 ? _translateOptions[0] : null);
        SelectedRewriteModeOption = FindOption(_rewriteOptions, o => o.Mode == previousRewriteMode) ?? (_rewriteOptions.Count > 0 ? _rewriteOptions[0] : null);

        TranslateLanguageComboBox.SelectedItem = SelectedTranslateLanguageOption;
        RewriteModeComboBox.SelectedItem = SelectedRewriteModeOption;
        UpdateRewriteOptionState();
    }

    private static T? FindOption<T>(IReadOnlyList<T> options, Func<T, bool> predicate) where T : class
    {
        foreach (var option in options)
        {
            if (predicate(option))
            {
                return option;
            }
        }

        return null;
    }

    private void UpdateActionAvailability()
    {
        TranslateSegment.Visibility = HasAction(AiActionType.Translate) ? Visibility.Visible : Visibility.Collapsed;
        RewriteSegment.Visibility = HasAction(AiActionType.Rewrite) ? Visibility.Visible : Visibility.Collapsed;
        SummarizeSegment.Visibility = HasAction(AiActionType.Summarize) ? Visibility.Visible : Visibility.Collapsed;
        SummarizeCachedIndicator.Visibility = HasAction(AiActionType.Summarize) && _hasCachedSummary ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool HasAction(AiActionType action) => (AvailableActions & action) == action;

    private AiActionType SelectDefaultAction()
    {
        if (HasAction(AiActionType.Translate))
        {
            return AiActionType.Translate;
        }

        if (HasAction(AiActionType.Rewrite))
        {
            return AiActionType.Rewrite;
        }

        if (HasAction(AiActionType.Summarize))
        {
            return AiActionType.Summarize;
        }

        return AiActionType.None;
    }

    private void ApplySelectedAction(AiActionType action)
    {
        if (action is AiActionType.Translate or AiActionType.Rewrite)
        {
            _lastConfigurableAction = action;
        }

        ActionSelector.SelectedItem = action switch
        {
            AiActionType.Translate => TranslateSegment,
            AiActionType.Rewrite => RewriteSegment,
            AiActionType.Summarize => SummarizeSegment,
            _ => null
        };

        TranslateOptionsPanel.Visibility = action == AiActionType.Translate ? Visibility.Visible : Visibility.Collapsed;
        RewriteOptionsPanel.Visibility = action == AiActionType.Rewrite ? Visibility.Visible : Visibility.Collapsed;
        SummarizeOptionsPanel.Visibility = action == AiActionType.Summarize ? Visibility.Visible : Visibility.Collapsed;
    }

    public async Task RefreshAvailabilityAsync()
    {
        if (_isRefreshing || _disposedValue)
        {
            return;
        }

        _isRefreshing = true;
        SetBusyUi(isBusy: false, showLoading: true);

        try
        {
            var account = await _profileService.GetAuthenticatedAccountAsync().ConfigureAwait(true);
            if (account == null)
            {
                UpdateUsageSummary(string.Empty);
                UpdatePanelState(showSignedOut: true);
                return;
            }

            var hasAiPack = await _storeManagementService.HasProductAsync(WinoAddOnProductType.AI_PACK).ConfigureAwait(true);
            if (!hasAiPack)
            {
                UpdateUsageSummary(string.Empty);
                UpdatePanelState(showPurchase: true);
                return;
            }

            var aiStatusResponse = await _profileService.GetAiStatusAsync().ConfigureAwait(true);
            if (aiStatusResponse.IsSuccess && aiStatusResponse.Result != null)
            {
                UpdateUsageSummary(
                    CreateUsageSummary(aiStatusResponse.Result),
                    GetUsedCount(aiStatusResponse.Result),
                    GetUsageLimit(aiStatusResponse.Result));
            }
            else
            {
                UpdateUsageSummary(Translator.WinoAccount_Management_AiPackUsageLoadFailed);
            }

            await RefreshCachedSummaryStateAsync().ConfigureAwait(true);
            ApplySelectedAction(SelectDefaultAction());
            UpdatePanelState(showReady: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            UpdateUsageSummary(Translator.WinoAccount_Management_AiPackUsageLoadFailed);
            UpdatePanelState(showReady: true);
        }
        finally
        {
            _isRefreshing = false;
            SetBusyUi(_isBusy, showLoading: false);
        }
    }

    private static string CreateUsageSummary(AiStatusResultDto aiStatus)
    {
        if (aiStatus.Used is int used && aiStatus.MonthlyLimit is int limit && limit > 0)
        {
            return string.Format(Translator.AiActions_UsageSummary, used, limit);
        }

        return Translator.WinoAccount_Management_AiPackUsageLoadFailed;
    }

    private static int GetUsedCount(AiStatusResultDto aiStatus)
        => aiStatus.Used is int used ? used : 0;

    private static string CreateUsageSummary(QuotaInfoDto quotaInfo)
    {
        if (quotaInfo.Used is int used && quotaInfo.MonthlyLimit is int limit && limit > 0)
        {
            return string.Format(Translator.AiActions_UsageSummary, used, limit);
        }

        return Translator.WinoAccount_Management_AiPackUsageLoadFailed;
    }

    private static int GetUsedCount(QuotaInfoDto quotaInfo)
        => quotaInfo.Used is int used ? used : 0;

    private static int GetUsageLimit(QuotaInfoDto quotaInfo)
        => quotaInfo.MonthlyLimit is int limit && limit > 0 ? limit : 1000;

    private static int GetUsageLimit(AiStatusResultDto aiStatus)
        => aiStatus.MonthlyLimit is int limit && limit > 0 ? limit : 1000;


    private void UpdatePanelState(bool showLoading = false, bool showSignedOut = false, bool showPurchase = false, bool showReady = false)
    {
        LoadingPanel.Visibility = showLoading ? Visibility.Visible : Visibility.Collapsed;
        SignedOutPanel.Visibility = showSignedOut ? Visibility.Visible : Visibility.Collapsed;
        PurchasePanel.Visibility = showPurchase ? Visibility.Visible : Visibility.Collapsed;
        ReadyPanel.Visibility = showReady ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateUsageSummary(string usageText, int usedCount = 0)
    {
        UsageSummaryTextBlock.Text = usageText;
        UsageProgressBar.Maximum = 1000;
        UsageProgressBar.Value = Math.Min(usedCount, 1000);
    }

    private void UpdateUsageSummary(string usageText, int usedCount, int usageLimit)
    {
        var normalizedLimit = usageLimit > 0 ? usageLimit : 1000;
        UsageSummaryTextBlock.Text = usageText;
        UsageProgressBar.Maximum = normalizedLimit;
        UsageProgressBar.Value = Math.Min(usedCount, normalizedLimit);
    }

    private void SetBusyUi(bool isBusy, bool showLoading)
    {
        _isBusy = isBusy;

        BusyProgressBar.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        ActionSelector.IsEnabled = !isBusy;
        TranslateLanguageComboBox.IsEnabled = !isBusy;
        RewriteModeComboBox.IsEnabled = !isBusy;
        CustomRewriteTextBox.IsEnabled = !isBusy;
        RunTranslateButton.IsEnabled = !isBusy;
        RunRewriteButton.IsEnabled = !isBusy;
        RunSummarizeButton.IsEnabled = !isBusy;
        SignedOutPanel.IsHitTestVisible = !isBusy;
        PurchasePanel.IsHitTestVisible = !isBusy;

        if (showLoading)
        {
            UpdatePanelState(showLoading: true);
        }
        else if (ReadyPanel.Visibility == Visibility.Visible)
        {
            UpdatePanelState(showReady: true);
        }
        else if (SignedOutPanel.Visibility == Visibility.Visible)
        {
            UpdatePanelState(showSignedOut: true);
        }
        else if (PurchasePanel.Visibility == Visibility.Visible)
        {
            UpdatePanelState(showPurchase: true);
        }
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var account = await _dialogService.ShowWinoAccountLoginDialogAsync();
        if (account != null)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Info, string.Format(Translator.WinoAccount_LoginSuccessMessage, account.Email), InfoBarMessageType.Success);
        }

        await RefreshAvailabilityAsync();
    }

    private async void CreateAccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var account = await _dialogService.ShowWinoAccountRegistrationDialogAsync();
        if (account != null)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Info, string.Format(Translator.WinoAccount_RegisterSuccessMessage, account.Email), InfoBarMessageType.Success);
        }

        await RefreshAvailabilityAsync();
    }

    private async void PurchaseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        SetBusyUi(isBusy: true, showLoading: false);

        try
        {
            var purchaseResult = await _storeManagementService.PurchaseAsync(WinoAddOnProductType.AI_PACK).ConfigureAwait(true);

            if (purchaseResult == StorePurchaseResult.NotPurchased)
            {
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, Translator.WinoAccount_Management_PurchaseStartFailed, InfoBarMessageType.Error);
                return;
            }

            var syncResult = await _profileService.SyncStoreEntitlementsAsync().ConfigureAwait(true);
            if (!syncResult.IsSuccess && !string.Equals(syncResult.ErrorCode, "MissingAccessToken", StringComparison.Ordinal))
            {
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, Translator.WinoAccount_Management_StoreSyncFailed, InfoBarMessageType.Error);
                return;
            }

            if (purchaseResult == StorePurchaseResult.AlreadyPurchased)
            {
                _dialogService.InfoBarMessage(Translator.Info_PurchaseExistsTitle, Translator.Info_PurchaseExistsMessage, InfoBarMessageType.Warning);
            }
            else
            {
                _dialogService.InfoBarMessage(Translator.Info_PurchaseThankYouTitle, Translator.Info_PurchaseThankYouMessage, InfoBarMessageType.Success);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, Translator.WinoAccount_Management_PurchaseStartFailed, InfoBarMessageType.Error);
        }
        finally
        {
            SetBusyUi(isBusy: false, showLoading: false);
            await RefreshAvailabilityAsync();
        }
    }

    private void ActionSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(ActionSelector.SelectedItem, TranslateSegment))
        {
            ApplySelectedAction(AiActionType.Translate);
            return;
        }

        if (ReferenceEquals(ActionSelector.SelectedItem, RewriteSegment))
        {
            ApplySelectedAction(AiActionType.Rewrite);
            return;
        }

        if (ReferenceEquals(ActionSelector.SelectedItem, SummarizeSegment))
        {
            ApplySelectedAction(AiActionType.Summarize);
            _ = RefreshCachedSummaryStateAsync();
        }
    }

    private void TranslateLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TranslateLanguageComboBox.SelectedItem is AiTranslateLanguageOption option)
        {
            SelectedTranslateLanguageOption = option;
        }
    }

    private void RewriteModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RewriteModeComboBox.SelectedItem is AiRewriteModeOption option)
        {
            SelectedRewriteModeOption = option;
            UpdateRewriteOptionState();
        }
    }

    private void UpdateRewriteOptionState()
    {
        var isCustom = SelectedRewriteModeOption?.IsCustom ?? false;
        RewriteDescriptionTextBlock.Text = SelectedRewriteModeOption?.Description ?? string.Empty;
        RewriteDescriptionTextBlock.Visibility = string.IsNullOrWhiteSpace(RewriteDescriptionTextBlock.Text) ? Visibility.Collapsed : Visibility.Visible;
        CustomRewriteTextBox.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RunTranslateButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteAiActionAsync(AiActionType.Translate);
    }

    private async void RunRewriteButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteAiActionAsync(AiActionType.Rewrite);
    }

    private async void RunSummarizeButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteAiActionAsync(AiActionType.Summarize);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CancelPendingOperation();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task ExecuteAiActionAsync(AiActionType action)
    {
        if (_isBusy)
        {
            _dialogService.InfoBarMessage(Translator.Composer_AiBusyTitle, Translator.Composer_AiBusyMessage, InfoBarMessageType.Warning);
            return;
        }

        if (HtmlHost == null)
        {
            return;
        }

        CancelAndDisposeActionCancellationToken();
        _actionCancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _actionCancellationTokenSource.Token;

        SetBusyUi(isBusy: true, showLoading: false);

        try
        {
            if (action == AiActionType.Translate && SelectedTranslateLanguageOption == null)
            {
                _dialogService.InfoBarMessage(Translator.Composer_AiErrorTitle, Translator.WinoAccount_Error_ValidationFailed, InfoBarMessageType.Error);
                return;
            }

            if (action == AiActionType.Rewrite && string.IsNullOrWhiteSpace(ResolveRewriteMode()))
            {
                _dialogService.InfoBarMessage(Translator.Composer_AiErrorTitle, Translator.WinoAccount_Error_ValidationFailed, InfoBarMessageType.Error);
                return;
            }

            if (action == AiActionType.Translate)
            {
                var cachedTranslation = await HtmlHost.TryGetCachedTranslationHtmlAsync(SelectedTranslateLanguageOption?.Code ?? string.Empty, cancellationToken).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();

                if (!string.IsNullOrWhiteSpace(cachedTranslation))
                {
                    await HtmlHost.ApplyHtmlResultAsync(cachedTranslation, cancellationToken).ConfigureAwait(true);
                    return;
                }
            }

            if (action == AiActionType.Summarize)
            {
                var cachedSummary = await HtmlHost.TryGetCachedSummaryTextAsync(cancellationToken).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();

                if (!string.IsNullOrWhiteSpace(cachedSummary))
                {
                    _hasCachedSummary = true;
                    UpdateActionAvailability();
                    await ShowSummaryDialogAsync(cachedSummary).ConfigureAwait(true);
                    return;
                }
            }

            var html = await HtmlHost.GetCurrentHtmlAsync(cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(html))
            {
                _dialogService.InfoBarMessage(Translator.Composer_AiErrorTitle, Translator.WinoAccount_Error_AiHtmlEmpty, InfoBarMessageType.Error);
                return;
            }

            var response = action switch
            {
                AiActionType.Translate => await _profileService.TranslateAsync(html, SelectedTranslateLanguageOption?.Code ?? string.Empty, cancellationToken).ConfigureAwait(true),
                AiActionType.Rewrite => await _profileService.RewriteAsync(html, ResolveRewriteMode(), cancellationToken).ConfigureAwait(true),
                AiActionType.Summarize => await _profileService.SummarizeAsync(html, cancellationToken).ConfigureAwait(true),
                _ => ApiEnvelope<AiTextResultDto>.Failure(ApiErrorCodes.ValidationFailed)
            };

            cancellationToken.ThrowIfCancellationRequested();

            if (!response.IsSuccess || response.Result == null || string.IsNullOrWhiteSpace(response.Result.Html))
            {
                _dialogService.InfoBarMessage(Translator.Composer_AiErrorTitle, WinoAccountAiErrorTranslator.Format(response.ErrorCode, null), InfoBarMessageType.Error);
                return;
            }

            if (response.Quota != null)
            {
                UpdateUsageSummary(
                    CreateUsageSummary(response.Quota),
                    GetUsedCount(response.Quota),
                    GetUsageLimit(response.Quota));
            }

            if (action == AiActionType.Translate)
            {
                await HtmlHost.SaveCachedTranslationHtmlAsync(SelectedTranslateLanguageOption?.Code ?? string.Empty, response.Result.Html, cancellationToken).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                await HtmlHost.ApplyHtmlResultAsync(response.Result.Html, cancellationToken).ConfigureAwait(true);
                return;
            }

            if (action == AiActionType.Summarize)
            {
                await HtmlHost.SaveCachedSummaryTextAsync(response.Result.Html, cancellationToken).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();

                _hasCachedSummary = true;
                UpdateActionAvailability();

                var savedSummary = await HtmlHost.TryGetCachedSummaryTextAsync(cancellationToken).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();

                await ShowSummaryDialogAsync(string.IsNullOrWhiteSpace(savedSummary) ? response.Result.Html : savedSummary).ConfigureAwait(true);
                return;
            }

            await HtmlHost.ApplyHtmlResultAsync(response.Result.Html, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(Translator.Composer_AiErrorTitle, WinoAccountAiErrorTranslator.Format(null, ex.Message), InfoBarMessageType.Error);
        }
        finally
        {
            SetBusyUi(isBusy: false, showLoading: false);

            if (_actionCancellationTokenSource != null)
            {
                _actionCancellationTokenSource.Dispose();
                _actionCancellationTokenSource = null;
            }

            // Summarize no longer auto-switches back; the user explicitly selected the tab.
        }
    }

    private string ResolveRewriteMode()
    {
        if (SelectedRewriteModeOption == null)
        {
            return string.Empty;
        }

        if (!SelectedRewriteModeOption.IsCustom)
        {
            return SelectedRewriteModeOption.Mode;
        }

        return CustomRewriteTextBox.Text?.Trim() ?? string.Empty;
    }

    private async Task RefreshCachedSummaryStateAsync()
    {
        if (HtmlHost == null || !HasAction(AiActionType.Summarize))
        {
            _hasCachedSummary = false;
            UpdateActionAvailability();
            return;
        }

        try
        {
            var cachedSummary = await HtmlHost.TryGetCachedSummaryTextAsync(CancellationToken.None).ConfigureAwait(true);
            _hasCachedSummary = !string.IsNullOrWhiteSpace(cachedSummary);
        }
        catch (Exception)
        {
            _hasCachedSummary = false;
        }

        UpdateActionAvailability();
    }

    private async Task ShowSummaryDialogAsync(string summary)
    {
        if (HtmlHost == null)
        {
            await _dialogService.ShowMessageAsync(summary, Translator.Composer_AiSummarize, WinoCustomMessageDialogIcon.Information);
            return;
        }

        var summaryTextBox = new TextBox
        {
            Text = summary,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 240,
            MaxHeight = 420,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = Translator.Composer_AiSummarize,
            PrimaryButtonText = Translator.Buttons_Save,
            SecondaryButtonText = Translator.Buttons_Close,
            DefaultButton = ContentDialogButton.Secondary,
            Content = new ScrollViewer
            {
                Content = summaryTextBox,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            }
        };

        dialog.PrimaryButtonClick += async (sender, args) =>
        {
            var deferral = args.GetDeferral();

            try
            {
                var path = await _dialogService.PickFilePathAsync(HtmlHost.GetSuggestedSummaryFileName());
                if (string.IsNullOrWhiteSpace(path))
                {
                    args.Cancel = true;
                    return;
                }

                await File.WriteAllTextAsync(path, summary);
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Info, string.Format(Translator.ClipboardTextCopied_Message, Path.GetFileName(path)), InfoBarMessageType.Success);
            }
            catch (Exception ex)
            {
                args.Cancel = true;
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, InfoBarMessageType.Error);
            }
            finally
            {
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
    }

    private void CancelAndDisposeActionCancellationToken()
    {
        if (_actionCancellationTokenSource == null)
        {
            return;
        }

        _actionCancellationTokenSource.Cancel();
        _actionCancellationTokenSource.Dispose();
        _actionCancellationTokenSource = null;
    }
}
