using System;
using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using EmailValidation;
using Fernandezja.ColorHashSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI;

namespace Wino.Controls;

/// <summary>
/// Contact avatar control built on top of PersonPicture.
/// Priority:
/// 1) AccountContact file-based picture
/// 2) Gravatar thumbnail (if enabled)
/// 3) Initials from display name fallback
/// </summary>
public sealed partial class ImagePreviewControl : PersonPicture, IDisposable
{
    private sealed record RefreshSnapshot(
        string DisplayName,
        string Address,
        Guid? ContactPictureFileId,
        string IdentityKey);

    private static readonly TimeSpan RefreshDebounceDuration = TimeSpan.FromMilliseconds(40);
    private static readonly ColorHash AvatarColorHash = new(new Options
    {
        S = new ArrayList { 0.5 },
        L = new ArrayList { 0.4 }
    });

    [GeneratedDependencyProperty]
    public partial IMailItemDisplayInformation? MailItemInformation { get; set; }

    [GeneratedDependencyProperty]
    public partial AccountContact? PreviewContact { get; set; }

    [GeneratedDependencyProperty]
    public partial string? Address { get; set; }

    [GeneratedDependencyProperty]
    public partial string? DisplayNameOverride { get; set; }

    private readonly IThumbnailService? _thumbnailService;
    private readonly IPreferencesService? _preferencesService;
    private readonly IContactPictureFileService? _contactPictureFileService;
    private INotifyPropertyChanged? _mailItemInformationPropertySource;
    private CancellationTokenSource? _refreshCancellationTokenSource;
    private DispatcherQueueTimer? _scheduledRefreshTimer;
    private long _refreshVersion;
    private string? _visibleIdentityKey;
    private string? _appliedPictureKey;

    public ImagePreviewControl()
    {
        DefaultStyleKey = typeof(PersonPicture);
        IsTabStop = false;

        try
        {
            _thumbnailService = App.Current.Services.GetService<IThumbnailService>();
            _preferencesService = App.Current.Services.GetService<IPreferencesService>();
            _contactPictureFileService = App.Current.Services.GetService<IContactPictureFileService>();
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

        RequestRefresh(resetIfIdentityChanged: true);
    }

    partial void OnPreviewContactPropertyChanged(DependencyPropertyChangedEventArgs e) => RequestRefresh(resetIfIdentityChanged: true);
    partial void OnAddressPropertyChanged(DependencyPropertyChangedEventArgs e) => RequestRefresh(resetIfIdentityChanged: true);
    partial void OnDisplayNameOverridePropertyChanged(DependencyPropertyChangedEventArgs e) => RequestRefresh(resetIfIdentityChanged: true);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_mailItemInformationPropertySource == null &&
            MailItemInformation is INotifyPropertyChanged observableMailItemInformation)
        {
            _mailItemInformationPropertySource = observableMailItemInformation;
            _mailItemInformationPropertySource.PropertyChanged += MailItemInformationPropertyChanged;
        }

        RequestRefresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => Dispose();

    public void Dispose()
    {
        if (_mailItemInformationPropertySource != null)
        {
            _mailItemInformationPropertySource.PropertyChanged -= MailItemInformationPropertyChanged;
            _mailItemInformationPropertySource = null;
        }

        CancelScheduledRefresh();
        CancelActiveRefresh();
        ProfilePicture = null;
        _visibleIdentityKey = null;
        _appliedPictureKey = null;
    }

    private void MailItemInformationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Refresh only for fields that affect avatar image or initials.
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName == nameof(IMailItemDisplayInformation.ContactPictureFileId)
            || e.PropertyName == nameof(IMailItemDisplayInformation.SenderContact)
            || e.PropertyName == nameof(IMailItemDisplayInformation.FromName)
            || e.PropertyName == nameof(IMailItemDisplayInformation.FromAddress)
            || e.PropertyName == nameof(IMailItemDisplayInformation.ThumbnailUpdatedEvent))
        {
            RequestRefresh(resetIfIdentityChanged: true);
        }
    }

    private void RequestRefresh(bool resetIfIdentityChanged = false)
    {
        if (DispatcherQueue == null || DispatcherQueue.HasThreadAccess)
        {
            if (resetIfIdentityChanged)
                ResetVisibleAvatarIfIdentityChanged();

            QueueRefresh();
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (resetIfIdentityChanged)
                ResetVisibleAvatarIfIdentityChanged();

            QueueRefresh();
        });
    }

    private void ResetVisibleAvatarIfIdentityChanged()
    {
        var address = ResolveAddress();
        var displayName = ResolveDisplayName(address);
        var contactPictureFileId = ResolveContactPictureFileId();
        var identityKey = BuildIdentityKey(address, contactPictureFileId);

        if (string.Equals(identityKey, _visibleIdentityKey, StringComparison.Ordinal))
        {
            if (ProfilePicture == null)
                ApplyFallbackVisualState(displayName);

            return;
        }

        _visibleIdentityKey = identityKey;
        _appliedPictureKey = null;
        ApplyFallbackVisualState(displayName);
    }

    private void QueueRefresh()
    {
        if (!IsLoaded)
            return;

        CancelScheduledRefresh();

        if (DispatcherQueue == null)
        {
            StartRefresh();
            return;
        }

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = RefreshDebounceDuration;
        timer.IsRepeating = false;
        timer.Tick += ScheduledRefreshTimerTick;
        _scheduledRefreshTimer = timer;

        timer.Start();
    }

    private void ScheduledRefreshTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        sender.Tick -= ScheduledRefreshTimerTick;

        if (_scheduledRefreshTimer != sender)
            return;

        _scheduledRefreshTimer = null;

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
        var timer = _scheduledRefreshTimer;
        _scheduledRefreshTimer = null;

        if (timer == null)
            return;

        timer.Stop();
        timer.Tick -= ScheduledRefreshTimerTick;
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

            await ApplyInitialVisualStateAsync(snapshot, refreshVersion, cancellationToken).ConfigureAwait(false);

            // Skip all picture loading if the user has disabled sender pictures.
            if (_preferencesService?.IsShowSenderPicturesEnabled == false)
                return;

            // 1) File-based contact picture (preferred — native WIC decode, no base64 overhead).
            if (snapshot.ContactPictureFileId.HasValue && _contactPictureFileService != null)
            {
                var filePath = _contactPictureFileService.GetContactPicturePath(snapshot.ContactPictureFileId.Value);
                if (!string.IsNullOrEmpty(filePath))
                {
                    var pictureKey = $"contact:{snapshot.ContactPictureFileId.Value:N}";
                    if (await IsPictureAlreadyAppliedAsync(pictureKey, refreshVersion, cancellationToken).ConfigureAwait(false))
                        return;

                    var fileBitmap = await CreateBitmapFromFileAsync(filePath, cancellationToken).ConfigureAwait(false);
                    if (fileBitmap != null)
                    {
                        await ApplyProfilePictureAsync(fileBitmap, pictureKey, refreshVersion, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }
            }

            // 2) Gravatar lookup through thumbnail service (if enabled).
            if (_preferencesService?.IsGravatarEnabled == true &&
                _thumbnailService != null &&
                !string.IsNullOrWhiteSpace(snapshot.Address) &&
                EmailValidator.Validate(snapshot.Address))
            {
                var thumbnailResult = await _thumbnailService
                    .GetThumbnailAsync(snapshot.Address.Trim().ToLowerInvariant(), awaitLoad: true)
                    .ConfigureAwait(false);

                if (thumbnailResult != null)
                {
                    var pictureKey = $"thumbnail:{thumbnailResult.FilePath}";
                    if (await IsPictureAlreadyAppliedAsync(pictureKey, refreshVersion, cancellationToken).ConfigureAwait(false))
                        return;

                    var thumbnailBitmap = await CreateBitmapFromFileAsync(thumbnailResult.FilePath, cancellationToken).ConfigureAwait(false);
                    if (thumbnailBitmap != null)
                    {
                        await ApplyProfilePictureAsync(thumbnailBitmap, pictureKey, refreshVersion, cancellationToken).ConfigureAwait(false);
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
            var contactPictureFileId = ResolveContactPictureFileId();
            var identityKey = BuildIdentityKey(address, contactPictureFileId);

            return new RefreshSnapshot(displayName, address, contactPictureFileId, identityKey);
        }).ConfigureAwait(false);
    }

    private Guid? ResolveContactPictureFileId()
        => PreviewContact?.ContactPictureFileId
           ?? MailItemInformation?.SenderContact?.ContactPictureFileId
           ?? MailItemInformation?.ContactPictureFileId;

    private static string BuildIdentityKey(string address, Guid? contactPictureFileId)
        => contactPictureFileId.HasValue
            ? $"contact:{contactPictureFileId.Value:N}"
            : $"address:{address.Trim().ToLowerInvariant()}";

    private string ResolveAddress()
    {
        if (!string.IsNullOrWhiteSpace(PreviewContact?.Address))
            return PreviewContact.Address.Trim();

        if (!string.IsNullOrWhiteSpace(Address))
            return Address.Trim();

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
        if (!string.IsNullOrWhiteSpace(PreviewContact?.Name))
            return PreviewContact.Name.Trim();

        if (!string.IsNullOrWhiteSpace(DisplayNameOverride))
            return DisplayNameOverride.Trim();

        var contactName = MailItemInformation?.SenderContact?.Name;
        if (!string.IsNullOrWhiteSpace(contactName))
            return contactName.Trim();

        if (!string.IsNullOrWhiteSpace(MailItemInformation?.FromName))
            return MailItemInformation.FromName.Trim();

        return resolvedAddress.Trim();
    }
    private async Task ApplyInitialVisualStateAsync(RefreshSnapshot snapshot, long refreshVersion, CancellationToken cancellationToken)
    {
        await ExecuteOnUiThreadAsync(() =>
        {
            if (!IsActiveRefresh(refreshVersion, cancellationToken))
                return;

            if (!string.Equals(snapshot.IdentityKey, _visibleIdentityKey, StringComparison.Ordinal))
            {
                _visibleIdentityKey = snapshot.IdentityKey;
                _appliedPictureKey = null;
                ApplyFallbackVisualState(snapshot.DisplayName);
            }
            else if (ProfilePicture == null)
            {
                ApplyFallbackVisualState(snapshot.DisplayName);
            }
        }).ConfigureAwait(false);
    }

    private void ApplyFallbackVisualState(string displayName)
    {
        var colorSource = string.IsNullOrWhiteSpace(displayName) ? "?" : displayName.Trim();
        var generatedColor = AvatarColorHash.BuildToColor(colorSource);

        DisplayName = displayName;
        Initials = null;
        ProfilePicture = null;
        Background = new SolidColorBrush(Color.FromArgb(255, generatedColor.R, generatedColor.G, generatedColor.B));
        Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
    }

    private async Task<bool> IsPictureAlreadyAppliedAsync(
        string pictureKey,
        long refreshVersion,
        CancellationToken cancellationToken)
    {
        return await ExecuteOnUiThreadAsync(() =>
            IsActiveRefresh(refreshVersion, cancellationToken) &&
            ProfilePicture != null &&
            string.Equals(pictureKey, _appliedPictureKey, StringComparison.Ordinal)).ConfigureAwait(false);
    }

    private async Task ApplyProfilePictureAsync(
        BitmapImage bitmapImage,
        string pictureKey,
        long refreshVersion,
        CancellationToken cancellationToken)
    {
        await ExecuteOnUiThreadAsync(() =>
        {
            if (!IsActiveRefresh(refreshVersion, cancellationToken))
                return;

            DisplayName = string.Empty;
            Initials = string.Empty;
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            ProfilePicture = bitmapImage;
            _appliedPictureKey = pictureKey;
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

    private async Task<BitmapImage?> CreateBitmapFromFileAsync(string filePath, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
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
            SetDecodePixelSize(bitmapImage);
            bitmapImage.SetSource(memoryStream.AsRandomAccessStream());
            return bitmapImage;
        }).ConfigureAwait(false);
    }

    private void SetDecodePixelSize(BitmapImage bitmapImage)
    {
        var requestedSize = Math.Max(ActualWidth, ActualHeight);

        if (double.IsNaN(requestedSize) || requestedSize <= 0)
        {
            requestedSize = Math.Max(Width, Height);
        }

        if (double.IsNaN(requestedSize) || requestedSize <= 0)
        {
            requestedSize = 48;
        }

        var decodePixelSize = Math.Max(1, (int)Math.Ceiling(requestedSize));

        bitmapImage.DecodePixelType = DecodePixelType.Logical;
        bitmapImage.DecodePixelWidth = decodePixelSize;
        bitmapImage.DecodePixelHeight = decodePixelSize;
    }
}
