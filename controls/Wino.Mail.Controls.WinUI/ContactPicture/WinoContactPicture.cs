using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Wino.Mail.Controls.Core;

namespace Wino.Mail.Controls.ContactPicture;

/// <summary>
/// PersonPicture that resolves a contact avatar with Local picture -> Gravatar -> Favicon -> Initials
/// priority. Bound identities (<see cref="IContactPicture"/>) are immutable; the
/// picture is resolved exactly once per identity and never re-reacts to property
/// changes. Safe under aggressive ListView container recycling: recycling both
/// cancels the in-flight load and changes the identity key, and an async result
/// is applied only after re-validating that identity on the UI thread.
/// </summary>
public sealed partial class WinoContactPicture : PersonPicture
{
    // Named ContactPicture (not Contact) because PersonPicture already exposes a
    // Contact property of type Windows.ApplicationModel.Contacts.Contact.
    public static readonly DependencyProperty ContactPictureProperty = DependencyProperty.Register(
        nameof(ContactPicture),
        typeof(object),
        typeof(WinoContactPicture),
        new PropertyMetadata(null, static (d, e) => ((WinoContactPicture)d).OnContactChanged(e.NewValue)));

    public static readonly DependencyProperty IsGravatarEnabledProperty = DependencyProperty.Register(
        nameof(IsGravatarEnabled),
        typeof(bool),
        typeof(WinoContactPicture),
        new PropertyMetadata(true));

    public static readonly DependencyProperty IsFaviconEnabledProperty = DependencyProperty.Register(
        nameof(IsFaviconEnabled),
        typeof(bool),
        typeof(WinoContactPicture),
        new PropertyMetadata(true));

    // All of this state is read and mutated exclusively on the UI thread, which
    // is what makes the identity re-check in ApplyImage race-free without locks.
    private CancellationTokenSource? _loadCancellation;
    private string? _identityKey;
    private string? _appliedImageKey;
    private string? _normalizedAddress;
    private string? _localImagePath;

    public WinoContactPicture()
    {
        DefaultStyleKey = typeof(PersonPicture);
        IsTabStop = false;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// The contact to display. Expected to implement <see cref="IContactPicture"/>;
    /// registered as object so no custom interface marshaling crosses the WinRT
    /// dependency property boundary.
    /// </summary>
    public object? ContactPicture
    {
        get => GetValue(ContactPictureProperty);
        set => SetValue(ContactPictureProperty, value);
    }

    /// <summary>
    /// Whether Gravatar lookup is attempted for the contact address. Read once
    /// when a load starts; runtime changes do not retrigger resolution.
    /// </summary>
    public bool IsGravatarEnabled
    {
        get => (bool)GetValue(IsGravatarEnabledProperty);
        set => SetValue(IsGravatarEnabledProperty, value);
    }

    /// <summary>
    /// Whether favicon lookup is attempted for the address domain. Read once
    /// when a load starts; runtime changes do not retrigger resolution.
    /// </summary>
    public bool IsFaviconEnabled
    {
        get => (bool)GetValue(IsFaviconEnabledProperty);
        set => SetValue(IsFaviconEnabledProperty, value);
    }

    private void OnContactChanged(object? newValue)
    {
        var contact = newValue as IContactPicture;
        var name = contact?.Name?.Trim() ?? string.Empty;
        var address = contact?.Address?.Trim().ToLowerInvariant() ?? string.Empty;
        var localImagePath = contact?.LocalImagePath;

        var contactKey = address.Length > 0
            ? "addr:" + address
            : name.Length > 0 ? "name:" + name : null;
        var newKey = contactKey is null
            ? null
            : localImagePath is null ? contactKey : $"{contactKey}|image:{localImagePath}";

        // Same identity re-bound (e.g. container reused for the same item after a
        // projection rebuild): keep whatever is already applied, no work at all.
        if (string.Equals(newKey, _identityKey, StringComparison.Ordinal)) return;

        CancelPendingLoad();
        _identityKey = newKey;
        _appliedImageKey = null;
        _normalizedAddress = address.Length > 0 ? address : null;
        _localImagePath = string.IsNullOrWhiteSpace(localImagePath) ? null : localImagePath;

        ProfilePicture = null;

        if (newKey is null)
        {
            Initials = string.Empty;
            Background = new SolidColorBrush(Colors.Transparent);
            return;
        }

        // Initials placeholder is applied synchronously so a freshly recycled row
        // is never blank. The generated background belongs to initials mode only.
        Initials = ContactVisual.GetInitials(name, address);
        Background = new SolidColorBrush(ContactVisual.GetBackgroundColor(name.Length > 0 ? name : address));
        Foreground = new SolidColorBrush(Colors.White);

        if (!IsLoaded || (_localImagePath is null &&
            (_normalizedAddress is null || (!IsGravatarEnabled && !IsFaviconEnabled)))) return;

        StartLoad();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The container was unloaded mid-fetch and reattached for the same item;
        // Contact did not change, so OnContactChanged will not re-fire. Resume.
        if (_identityKey is not null &&
            !string.Equals(_appliedImageKey, _identityKey, StringComparison.Ordinal) &&
            _loadCancellation is null &&
            (_localImagePath is not null ||
             (_normalizedAddress is not null && (IsGravatarEnabled || IsFaviconEnabled))))
        {
            StartLoad();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Cancel only. ListView raises Unloaded on containers it later reuses,
        // so applied picture and identity state must survive.
        CancelPendingLoad();
    }

    private void CancelPendingLoad()
    {
        if (_loadCancellation is null) return;

        _loadCancellation.Cancel();
        _loadCancellation.Dispose();
        _loadCancellation = null;
    }

    private void StartLoad()
    {
        var request = new LoadRequest(
            _identityKey!,
            _normalizedAddress,
            _localImagePath,
            IsGravatarEnabled,
            IsFaviconEnabled);

        _loadCancellation = new CancellationTokenSource();
        _ = LoadAsync(request, _loadCancellation.Token);
    }

    private async Task LoadAsync(LoadRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var imagePath = request.LocalImagePath;

            if (imagePath is null || !File.Exists(imagePath))
            {
                imagePath = request.Address is null
                    ? null
                    : await ContactThumbnailLoader.ResolveAsync(
                        request.Address,
                        request.IsGravatarEnabled,
                        request.IsFaviconEnabled,
                        cancellationToken).ConfigureAwait(false);
            }

            // Null means no Gravatar and no favicon: the initials placeholder is final.
            if (imagePath is null) return;

            var bytes = await File.ReadAllBytesAsync(imagePath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            DispatcherQueue.TryEnqueue(() => ApplyImage(request, bytes, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            // Expected when the container recycles or unloads mid-load.
        }
        catch
        {
            // Any IO/decode failure leaves the initials placeholder in place.
        }
    }

    private void ApplyImage(LoadRequest request, byte[] bytes, CancellationToken cancellationToken)
    {
        // Both guards run on the UI thread, the only thread mutating this state:
        // a stale chain from a recycled identity fails one of the two checks.
        if (cancellationToken.IsCancellationRequested) return;
        if (!string.Equals(request.IdentityKey, _identityKey, StringComparison.Ordinal)) return;

        try
        {
            var bitmap = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Logical,
                DecodePixelWidth = ContactThumbnailLoader.AvatarPixelSize,
                DecodePixelHeight = ContactThumbnailLoader.AvatarPixelSize,
            };

            using var stream = new MemoryStream(bytes);
            bitmap.SetSource(stream.AsRandomAccessStream());

            Initials = string.Empty;
            Background = new SolidColorBrush(Colors.Transparent);
            ProfilePicture = bitmap;
            _appliedImageKey = request.IdentityKey;
        }
        catch
        {
            // Undecodable cache payload; initials placeholder remains.
        }
    }

    /// <summary>
    /// Everything the async load chain needs, snapshotted on the UI thread at
    /// load start — dependency properties are never read off-thread.
    /// </summary>
    private sealed record LoadRequest(
        string IdentityKey,
        string? Address,
        string? LocalImagePath,
        bool IsGravatarEnabled,
        bool IsFaviconEnabled);
}
