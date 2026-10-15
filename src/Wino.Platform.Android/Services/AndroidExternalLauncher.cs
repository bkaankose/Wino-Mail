using Android.App;
using Android.Content;
using Android.Webkit;
using AndroidX.Browser.CustomTabs;
using AndroidX.Core.Content;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;
using AndroidUri = Android.Net.Uri;

namespace Wino.Platform.Android.Services;

/// <summary>
/// Web links open in a Custom Tab (the browser falls back to a plain view intent when it has no Custom Tabs support),
/// <c>mailto:</c> links through <c>ACTION_SENDTO</c>. Files open with <c>ACTION_VIEW</c> on a FileProvider content URI with a
/// read grant: the file is copied into <see cref="AndroidPaths.SharedFilesRoot"/> first, because the provider exposes no
/// other folder. Intents start from the head's current activity, otherwise from the application with a new task.
/// </summary>
public sealed class AndroidExternalLauncher(Context context, AndroidPaths paths, IDispatcher dispatcher, Func<Activity?> currentActivity) : IExternalLauncher
{
    /// <summary>Copies older than this are deleted when the next file opens.</summary>
    private static readonly TimeSpan SharedCopyLifetime = TimeSpan.FromDays(1);

    /// <summary>The FileProvider authority the head's manifest declares: "&lt;package name&gt;.fileprovider".</summary>
    public static string GetFileProviderAuthority(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return (context.PackageName ?? throw new InvalidOperationException("The package name is unavailable.")) + ".fileprovider";
    }

    public async Task<PlatformOperationResult> LaunchFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return new(PlatformOperationStatus.Failed, "The file does not exist.");

        var shared = await Task.Run(() => CopyToSharedFolder(Path.GetFullPath(path), cancellationToken), cancellationToken).ConfigureAwait(false);
        var extension = Path.GetExtension(shared).TrimStart('.').ToLowerInvariant();
        var mimeType = (extension.Length > 0 ? MimeTypeMap.Singleton?.GetMimeTypeFromExtension(extension) : null) ?? "application/octet-stream";

        PlatformOperationResult result = new(PlatformOperationStatus.Failed);
        await dispatcher.ExecuteOnUIThread(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = FileProvider.GetUriForFile(context, GetFileProviderAuthority(context), new Java.IO.File(shared));
            if (uri is null) { result = new(PlatformOperationStatus.Failed, "The file cannot be shared."); return; }
            var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(uri, mimeType);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            result = Start(intent, "No app can open this file.");
        }).ConfigureAwait(false);
        return result;
    }

    public async Task<PlatformOperationResult> LaunchUriAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("https" or "http" or "mailto"))
            return new PlatformOperationResult(PlatformOperationStatus.Failed, "Unsupported external URL scheme.");
        cancellationToken.ThrowIfCancellationRequested();

        PlatformOperationResult result = new(PlatformOperationStatus.Failed);
        await dispatcher.ExecuteOnUIThread(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nativeUri = AndroidUri.Parse(uri.AbsoluteUri);
            if (nativeUri is null) { result = new(PlatformOperationStatus.Failed, "The address is not a valid URL."); return; }
            if (uri.Scheme == "mailto")
            {
                result = Start(new Intent(Intent.ActionSendto, nativeUri), "No app can send mail.");
                return;
            }

            var customTab = new CustomTabsIntent.Builder().SetShowTitle(true).Build();
            var intent = customTab.Intent;
            intent.SetData(nativeUri);
            result = Start(intent, "No app can open this link.");
        }).ConfigureAwait(false);
        return result;
    }

    /// <summary>Starts <paramref name="intent"/> on the main thread; Unavailable when no app handles it.</summary>
    private PlatformOperationResult Start(Intent intent, string unavailableMessage)
    {
        var activity = currentActivity();
        try
        {
            if (activity is not null && !activity.IsFinishing) activity.StartActivity(intent);
            else
            {
                intent.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(intent);
            }
            return new(PlatformOperationStatus.Succeeded);
        }
        catch (ActivityNotFoundException) { return new(PlatformOperationStatus.Unavailable, unavailableMessage); }
    }

    /// <summary>Copies the file into a fresh folder under the shared root (keeping its name) and removes stale copies.</summary>
    private string CopyToSharedFolder(string source, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(paths.SharedFilesRoot);
        Directory.CreateDirectory(root);
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (DateTime.UtcNow - Directory.GetCreationTimeUtc(folder) > SharedCopyLifetime) Directory.Delete(folder, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var destinationFolder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destinationFolder);
        var destination = Path.Combine(destinationFolder, Path.GetFileName(source));
        File.Copy(source, destination);
        return destination;
    }
}
