using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using Wino.Core.Domain.Models.Common;
using Wino.Platform.Android.Authentication;
using AndroidUri = Android.Net.Uri;

namespace Wino.Mail.Android.Infrastructure;

/// <summary>
/// The system pickers for files the user chooses (the Android twin of the Mac NSOpenPanel and the iOS document picker):
/// the Storage Access Framework (<c>ACTION_OPEN_DOCUMENT</c>) for attachments and the system photo picker
/// (<c>ACTION_PICK_IMAGES</c> on Android 13 and later, <c>ACTION_OPEN_DOCUMENT</c> with <c>image/*</c> before) for
/// inline pictures. No storage permission is needed: the picker grants access to the chosen items only. Picked files are
/// copied into the cache under their display names so the shared <see cref="SharedFile"/> keeps a real name and path.
/// </summary>
public sealed class AndroidDocumentPicker(AndroidActivityResults results, Context context)
{
    /// <summary>Attachments larger than this are skipped (the draft MIME holds them in memory).</summary>
    public const long MaximumAttachmentBytes = 50L * 1024 * 1024;

    /// <summary>Lets the user choose any number of documents; an empty list when cancelled.</summary>
    public async Task<List<SharedFile>> PickFilesAsync(CancellationToken cancellationToken = default)
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraAllowMultiple, true);
        var uris = await PickAsync(intent, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var files = new List<SharedFile>();
            foreach (var uri in uris)
            {
                try
                {
                    if (Copy(uri) is { } file) files.Add(file);
                }
                catch (Exception exception) when (exception is IOException or Java.Lang.Exception or UnauthorizedAccessException)
                {
                    Serilog.Log.Warning(exception, "A picked file could not be read.");
                }
            }
            return files;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lets the user choose pictures with the system photo picker; the content URIs, empty when cancelled.</summary>
    public Task<List<AndroidUri>> PickImagesAsync(CancellationToken cancellationToken = default)
    {
        Intent intent;
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            intent = new Intent(MediaStore.ActionPickImages);
            intent.PutExtra(MediaStore.ExtraPickImagesMax, Math.Min(10, MediaStore.PickImagesMaxLimit));
        }
        else
        {
            intent = new Intent(Intent.ActionOpenDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("image/*");
            intent.PutExtra(Intent.ExtraAllowMultiple, true);
        }
        return PickAsync(intent, cancellationToken);
    }

    private async Task<List<AndroidUri>> PickAsync(Intent intent, CancellationToken cancellationToken)
    {
        var result = await results.StartActivityForResultAsync(intent, cancellationToken).ConfigureAwait(false);
        var uris = new List<AndroidUri>();
        if (result.ResultCode != Result.Ok || result.Data is not { } data) return uris;
        if (data.ClipData is { } clip)
        {
            for (int index = 0; index < clip.ItemCount; index++)
                if (clip.GetItemAt(index)?.Uri is { } uri) uris.Add(uri);
        }
        else if (data.Data is { } single) uris.Add(single);
        return uris;
    }

    /// <summary>Copies one document into the cache; null when it is too large or unreadable.</summary>
    private SharedFile? Copy(AndroidUri uri)
    {
        var resolver = context.ContentResolver ?? throw new InvalidOperationException("The content resolver is unavailable.");
        var name = DisplayName(resolver, uri);
        using var input = resolver.OpenInputStream(uri);
        if (input is null) return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaximumAttachmentBytes)
            {
                Serilog.Log.Information("A picked file was larger than {Limit} bytes and was skipped.", MaximumAttachmentBytes);
                return null;
            }
        }
        var bytes = buffer.ToArray();
        var folder = Path.Combine(context.CacheDir?.AbsolutePath ?? Path.GetTempPath(), "picked", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, bytes);
        return new SharedFile(path, bytes);
    }

    /// <summary>The document's display name with path separators removed; a fallback when the provider has none.</summary>
    private static string DisplayName(ContentResolver resolver, AndroidUri uri)
    {
        string? name = null;
        using (var cursor = resolver.Query(uri, [IOpenableColumns.DisplayName], null, null, null))
        {
            if (cursor is not null && cursor.MoveToFirst())
            {
                int column = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
                if (column >= 0) name = cursor.GetString(column);
            }
        }
        name ??= uri.LastPathSegment;
        if (string.IsNullOrWhiteSpace(name)) name = "attachment";
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return name;
    }
}
