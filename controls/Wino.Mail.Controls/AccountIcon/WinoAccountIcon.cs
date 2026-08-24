using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wino.Mail.Controls.Core.AccountIcon;
using Windows.UI;

namespace Wino.Mail.Controls.AccountIcon;

/// <summary>
/// Displays an account profile picture when available, otherwise its provider glyph.
/// </summary>
public sealed partial class WinoAccountIcon : IconSourceElement
{
    private const double DefaultIconSize = 20d;

    private CancellationTokenSource? _loadCancellation;
    private long? _foregroundChangedCallbackToken;
    private int _presentationVersion;

    [GeneratedDependencyProperty]
    public partial IAccountIconInfo? Account { get; set; }

    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsProfilePictureEnabled { get; set; }

    [GeneratedDependencyProperty(DefaultValue = DefaultIconSize)]
    public partial double IconSize { get; set; }

    public WinoAccountIcon()
    {
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        UpdateSize();
        UpdatePresentation();
    }

    partial void OnAccountChanged(IAccountIconInfo? newValue) => UpdatePresentation();

    partial void OnIsProfilePictureEnabledChanged(bool newValue) => UpdatePresentation();

    partial void OnIconSizeChanged(double newValue)
    {
        UpdateSize();
        UpdatePresentation();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _foregroundChangedCallbackToken ??= RegisterPropertyChangedCallback(ForegroundProperty, OnForegroundChanged);
        UpdatePresentation();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_foregroundChangedCallbackToken is { } token)
        {
            UnregisterPropertyChangedCallback(ForegroundProperty, token);
            _foregroundChangedCallbackToken = null;
        }

        CancelPendingLoad();
    }

    private void OnForegroundChanged(DependencyObject sender, DependencyProperty property)
    {
        if (IconSource is not WinoFontIconSource providerIcon ||
            TryGetAccountColor(Account?.AccountColorHex, out _))
        {
            return;
        }

        providerIcon.Foreground = Foreground;
    }

    private void UpdateSize()
    {
        var size = GetEffectiveIconSize();
        Width = size;
        Height = size;
    }

    private void UpdatePresentation()
    {
        CancelPendingLoad();
        var presentationVersion = ++_presentationVersion;
        var account = Account;

        ShowProviderIcon(account);

        if (!IsLoaded || !IsProfilePictureEnabled || string.IsNullOrWhiteSpace(account?.ProfilePicturePath))
        {
            return;
        }

        var request = new LoadRequest(
            presentationVersion,
            account.ProfilePicturePath,
            account.AccountColorHex);
        _loadCancellation = new CancellationTokenSource();
        _ = LoadProfilePictureAsync(request, _loadCancellation.Token);
    }

    private void ShowProviderIcon(IAccountIconInfo? account)
    {
        if (account is null)
        {
            IconSource = null;
            return;
        }

        var iconSource = new WinoFontIconSource
        {
            Glyph = AccountIconGlyphs.GetGlyph(account.Provider),
            FontSize = GetEffectiveIconSize(),
        };

        if (TryGetAccountColor(account.AccountColorHex, out var color))
        {
            iconSource.Foreground = new SolidColorBrush(color);
        }
        else if (Foreground is not null)
        {
            iconSource.Foreground = Foreground;
        }

        IconSource = iconSource;
    }

    private async Task LoadProfilePictureAsync(LoadRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var iconUri = await AccountProfilePictureCache.GetIconUriAsync(
                request.ProfilePicturePath,
                request.AccountColorHex,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            DispatcherQueue.TryEnqueue(() => ApplyProfilePicture(request, iconUri, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            // Expected when the icon unloads or receives a new identity.
        }
        catch
        {
            // Any file, decode, or cache failure leaves the provider fallback visible.
        }
    }

    private void ApplyProfilePicture(LoadRequest request, Uri iconUri, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || request.PresentationVersion != _presentationVersion)
        {
            return;
        }

        IconSource = new BitmapIconSource
        {
            UriSource = iconUri,
            ShowAsMonochrome = false,
        };
    }

    private void CancelPendingLoad()
    {
        if (_loadCancellation is null)
        {
            return;
        }

        _loadCancellation.Cancel();
        _loadCancellation.Dispose();
        _loadCancellation = null;
    }

    private double GetEffectiveIconSize() => double.IsFinite(IconSize) && IconSize > 0
        ? IconSize
        : DefaultIconSize;

    private static bool TryGetAccountColor(string? accountColorHex, out Color color)
    {
        if (AccountProfilePictureRenderer.TryParseColor(accountColorHex, out var skiaColor))
        {
            color = Color.FromArgb(skiaColor.Alpha, skiaColor.Red, skiaColor.Green, skiaColor.Blue);
            return true;
        }

        color = default;
        return false;
    }

    private sealed record LoadRequest(
        int PresentationVersion,
        string ProfilePicturePath,
        string? AccountColorHex);
}
