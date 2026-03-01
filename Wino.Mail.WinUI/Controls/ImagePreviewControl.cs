using System;
using System.IO;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using EmailValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI;

namespace Wino.Controls;

/// <summary>
/// Contact avatar control built on top of PersonPicture.
/// Priority:
/// 1) AccountContact/Base64 picture
/// 2) Gravatar thumbnail (if enabled)
/// 3) Initials from display name fallback
/// </summary>
public sealed partial class ImagePreviewControl : PersonPicture
{
    private sealed record RefreshSnapshot(string DisplayName, string Address, string Base64Picture);

    private static readonly TimeSpan RefreshDebounceDuration = TimeSpan.FromMilliseconds(40);

    [GeneratedDependencyProperty]
    public partial IMailItemDisplayInformation? MailItemInformation { get; set; }

    private readonly IThumbnailService? _thumbnailService;
    private readonly IPreferencesService? _preferencesService;
    private INotifyPropertyChanged? _mailItemInformationPropertySource;
    private CancellationTokenSource? _refreshCancellationTokenSource;
    private CancellationTokenSource? _scheduledRefreshCancellationTokenSource;
    private long _refreshVersion;

    public ImagePreviewControl()
    {
        DefaultStyleKey = typeof(PersonPicture);

        try
        {
            _thumbnailService = App.Current.Services.GetService<IThumbnailService>();
            _preferencesService = App.Current.Services.GetService<IPreferencesService>();
        }
        catch
        {
            // Keep control functional in design-time/test contexts without service provider.
        }

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    partial void OnMailItemInformationPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        if (_mailItemInformationPropertySource != null)
        {
            _mailItemInformationPropertySource.PropertyChanged -= MailItemInformationPropertyChanged;
            _mailItemInformationPropertySource = null;
        }

        if (e.NewValue is INotifyPropertyChanged observableMailItemInformation)
        {
            _mailItemInformationPropertySource = observableMailItemInformation;
            _mailItemInformationPropertySource.PropertyChanged += MailItemInformationPropertyChanged;
        }

        RequestRefresh();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RequestRefresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_mailItemInformationPropertySource != null)
        {
            _mailItemInformationPropertySource.PropertyChanged -= MailItemInformationPropertyChanged;
            _mailItemInformationPropertySource = null;
        }

        CancelScheduledRefresh();
        CancelActiveRefresh();
    }

    private void MailItemInformationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Refresh only for fields that affect avatar image or initials.
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName == nameof(IMailItemDisplayInformation.Base64ContactPicture)
            || e.PropertyName == nameof(IMailItemDisplayInformation.SenderContact)
            || e.PropertyName == nameof(IMailItemDisplayInformation.FromName)
            || e.PropertyName == nameof(IMailItemDisplayInformation.FromAddress)
            || e.PropertyName == nameof(IMailItemDisplayInformation.ThumbnailUpdatedEvent))
        {
            RequestRefresh();
        }
    }

    private void RequestRefresh()
    {
        if (DispatcherQueue == null || DispatcherQueue.HasThreadAccess)
        {
            QueueRefresh();
            return;
        }

        DispatcherQueue.TryEnqueue(QueueRefresh);
    }

    private void QueueRefresh()
    {
        if (!IsLoaded)
            return;

        CancelScheduledRefresh();

        var cts = new CancellationTokenSource();
        _scheduledRefreshCancellationTokenSource = cts;

        _ = DebounceAndRefreshAsync(cts.Token);
    }

    private async Task DebounceAndRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(RefreshDebounceDuration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        StartRefresh();
    }

    private void StartRefresh()
    {
        CancelActiveRefresh();

        var cts = new CancellationTokenSource();
        _refreshCancellationTokenSource = cts;
        var refreshVersion = Interlocked.Increment(ref _refreshVersion);
        _ = RefreshAsync(refreshVersion, cts.Token);
    }

    private void CancelScheduledRefresh()
    {
        var cts = _scheduledRefreshCancellationTokenSource;
        _scheduledRefreshCancellationTokenSource = null;

        if (cts != null && !cts.IsCancellationRequested)
        {
            cts.Cancel();
        }

        cts?.Dispose();
    }

    private void CancelActiveRefresh()
    {
        var cts = _refreshCancellationTokenSource;
        _refreshCancellationTokenSource = null;

        if (cts != null && !cts.IsCancellationRequested)
        {
            cts.Cancel();
        }

        cts?.Dispose();
    }

    private async Task RefreshAsync(long refreshVersion, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await CaptureSnapshotAsync(refreshVersion, cancellationToken).ConfigureAwait(false);
            if (snapshot == null)
                return;

            await ApplyInitialVisualStateAsync(snapshot.DisplayName, refreshVersion, cancellationToken).ConfigureAwait(false);

            // 1) Explicit contact picture.
            if (!string.IsNullOrWhiteSpace(snapshot.Base64Picture))
            {
                var localBitmap = await CreateBitmapFromBase64Async(snapshot.Base64Picture, cancellationToken).ConfigureAwait(false);
                if (localBitmap != null)
                {
                    await ApplyProfilePictureAsync(localBitmap, refreshVersion, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            // 2) Gravatar lookup through thumbnail service (if enabled).
            if (_preferencesService?.IsGravatarEnabled == true &&
                _thumbnailService != null &&
                !string.IsNullOrWhiteSpace(snapshot.Address) &&
                EmailValidator.Validate(snapshot.Address))
            {
                var thumbnailBase64 = await _thumbnailService
                    .GetThumbnailAsync(snapshot.Address.Trim().ToLowerInvariant(), awaitLoad: true)
                    .ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(thumbnailBase64))
                {
                    var thumbnailBitmap = await CreateBitmapFromBase64Async(thumbnailBase64, cancellationToken).ConfigureAwait(false);
                    if (thumbnailBitmap != null)
                    {
                        await ApplyProfilePictureAsync(thumbnailBitmap, refreshVersion, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }
            }

            // 3) Initials fallback is already in place via DisplayName + ProfilePicture = null.
        }
        catch (OperationCanceledException)
        {
            // Expected during virtualization/recycling.
        }
        catch
        {
            // Keep fallback initials if decoding/network fails.
        }
    }

    // DependencyProperty-backed values must be read on UI thread once, then used off-thread.
    private async Task<RefreshSnapshot?> CaptureSnapshotAsync(long refreshVersion, CancellationToken cancellationToken)
    {
        return await ExecuteOnUiThreadAsync(() =>
        {
            if (!IsActiveRefresh(refreshVersion, cancellationToken))
                return null;

            var address = ResolveAddress();
            var displayName = ResolveDisplayName(address);
            var base64Picture = ResolveBase64Picture();

            return new RefreshSnapshot(displayName, address, base64Picture);
        }).ConfigureAwait(false);
    }

    private string ResolveAddress()
    {
        if (MailItemInformation == null)
            return string.Empty;

        var contactAddress = MailItemInformation?.SenderContact?.Address;
        if (!string.IsNullOrWhiteSpace(contactAddress))
            return contactAddress.Trim();

        if (!string.IsNullOrWhiteSpace(MailItemInformation?.FromAddress))
            return MailItemInformation.FromAddress.Trim();

        return string.Empty;
    }

    private string ResolveDisplayName(string resolvedAddress)
    {
        var contactName = MailItemInformation?.SenderContact?.Name;
        if (!string.IsNullOrWhiteSpace(contactName))
            return contactName.Trim();

        if (!string.IsNullOrWhiteSpace(MailItemInformation?.FromName))
            return MailItemInformation.FromName.Trim();

        return resolvedAddress.Trim();
    }

    private string ResolveBase64Picture()
    {
        if (!string.IsNullOrWhiteSpace(MailItemInformation?.SenderContact?.Base64ContactPicture))
            return MailItemInformation.SenderContact.Base64ContactPicture;

        if (!string.IsNullOrWhiteSpace(MailItemInformation?.Base64ContactPicture))
            return MailItemInformation.Base64ContactPicture;

        return string.Empty;
    }

    private async Task ApplyInitialVisualStateAsync(string displayName, long refreshVersion, CancellationToken cancellationToken)
    {
        await ExecuteOnUiThreadAsync(() =>
        {
            if (!IsActiveRefresh(refreshVersion, cancellationToken))
                return;

            DisplayName = displayName;
            ProfilePicture = null;
        }).ConfigureAwait(false);
    }

    private async Task ApplyProfilePictureAsync(BitmapImage bitmapImage, long refreshVersion, CancellationToken cancellationToken)
    {
        await ExecuteOnUiThreadAsync(() =>
        {
            if (!IsActiveRefresh(refreshVersion, cancellationToken))
                return;

            ProfilePicture = bitmapImage;
        }).ConfigureAwait(false);
    }

    private bool IsActiveRefresh(long refreshVersion, CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested && refreshVersion == _refreshVersion;

    private async Task ExecuteOnUiThreadAsync(Action action)
    {
        if (DispatcherQueue == null || DispatcherQueue.HasThreadAccess)
        {
            action();
            return;
        }

        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var enqueued = DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                action();
                completion.TrySetResult(null);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        if (!enqueued)
        {
            completion.TrySetException(new InvalidOperationException("Failed to dispatch UI update."));
        }

        await completion.Task.ConfigureAwait(false);
    }

    private async Task<T> ExecuteOnUiThreadAsync<T>(Func<T> func)
    {
        if (DispatcherQueue == null || DispatcherQueue.HasThreadAccess)
        {
            return func();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        var enqueued = DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                completion.TrySetResult(func());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        if (!enqueued)
        {
            completion.TrySetException(new InvalidOperationException("Failed to dispatch UI update."));
        }

        return await completion.Task.ConfigureAwait(false);
    }

    private async Task<BitmapImage?> CreateBitmapFromBase64Async(string base64, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(base64))
            return null;

        byte[] bytes;

        try
        {
            bytes = await Task.Run(() => Convert.FromBase64String(base64), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        return await ExecuteOnUiThreadAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var memoryStream = new MemoryStream(bytes);
            var bitmapImage = new BitmapImage();
            bitmapImage.SetSource(memoryStream.AsRandomAccessStream());
            return bitmapImage;
        }).ConfigureAwait(false);
    }

}
