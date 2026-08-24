using System.ComponentModel;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wino.Mail.Controls.Core.AccountIcon;
using Windows.UI;

namespace Wino.Mail.Controls.AccountIcon;

/// <summary>
/// Resolves account state to a concrete <see cref="Microsoft.UI.Xaml.Controls.IconSource"/>.
/// </summary>
/// <remarks>
/// WinUI's projected IconSource base is not composable by managed subclasses. Bind
/// <see cref="IconSource"/> to the consuming control's IconSource property instead.
/// </remarks>
public sealed partial class WinoAccountIconSource : DependencyObject, INotifyPropertyChanged, IDisposable
{
    private const double DefaultIconSize = 20d;

    private CancellationTokenSource? _loadCancellation;
    private IconSource? _iconSource;
    private int _presentationVersion;

    [GeneratedDependencyProperty]
    public partial IAccountIconInfo? Account { get; set; }

    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsProfilePictureEnabled { get; set; }

    [GeneratedDependencyProperty(DefaultValue = DefaultIconSize)]
    public partial double IconSize { get; set; }

    public WinoAccountIconSource()
    {
        UpdatePresentation();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets the concrete font or bitmap source for an IconSource property.
    /// </summary>
    public IconSource? IconSource
    {
        get => _iconSource;
        private set
        {
            if (ReferenceEquals(_iconSource, value))
            {
                return;
            }

            _iconSource = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconSource)));
        }
    }

    partial void OnAccountChanged(IAccountIconInfo? newValue) => UpdatePresentation();

    partial void OnIsProfilePictureEnabledChanged(bool newValue) => UpdatePresentation();

    partial void OnIconSizeChanged(double newValue) => UpdatePresentation();

    public void Dispose()
    {
        CancelPendingLoad();
        _presentationVersion++;
    }

    private void UpdatePresentation()
    {
        CancelPendingLoad();
        var presentationVersion = ++_presentationVersion;
        var account = Account;

        ShowProviderIcon(account);

        if (!IsProfilePictureEnabled || string.IsNullOrWhiteSpace(account?.ProfilePicturePath))
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

        var source = new WinoFontIconSource
        {
            Glyph = AccountIconGlyphs.GetGlyph(account.Provider),
            FontSize = GetEffectiveIconSize(),
        };

        if (TryGetAccountColor(account.AccountColorHex, out var color))
        {
            source.Foreground = new SolidColorBrush(color);
        }

        IconSource = source;
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
            // Expected when the source receives a new identity or is disposed.
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
