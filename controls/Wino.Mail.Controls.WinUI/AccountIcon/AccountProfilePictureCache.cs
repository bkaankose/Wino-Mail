using Windows.Storage;

namespace Wino.Mail.Controls.AccountIcon;

internal static class AccountProfilePictureCache
{
    private const string CacheFolderName = "wino-account-icons";

    public static async Task<Uri> GetIconUriAsync(
        string profilePicturePath,
        string? accountColorHex,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(profilePicturePath);
        if (!fileInfo.Exists || fileInfo.Length <= 0 || fileInfo.Length > AccountProfilePictureRenderer.MaximumInputBytes)
        {
            throw new FileNotFoundException("The profile picture is unavailable.", profilePicturePath);
        }

        var sourceBytes = await File.ReadAllBytesAsync(profilePicturePath, cancellationToken).ConfigureAwait(false);
        var cacheKey = AccountProfilePictureRenderer.GetCacheKey(sourceBytes, accountColorHex);
        var iconFileName = $"{cacheKey}.png";
        var cacheFolderPath = Path.Combine(ApplicationData.Current.TemporaryFolder.Path, CacheFolderName);
        var iconPath = Path.Combine(cacheFolderPath, iconFileName);

        if (!File.Exists(iconPath))
        {
            var renderedBytes = await Task.Run(
                () => AccountProfilePictureRenderer.Render(sourceBytes, accountColorHex),
                cancellationToken).ConfigureAwait(false);
            var temporaryPath = Path.Combine(cacheFolderPath, $"{cacheKey}.{Guid.NewGuid():N}.tmp");

            Directory.CreateDirectory(cacheFolderPath);

            try
            {
                await File.WriteAllBytesAsync(temporaryPath, renderedBytes, cancellationToken).ConfigureAwait(false);

                try
                {
                    File.Move(temporaryPath, iconPath);
                }
                catch (IOException) when (File.Exists(iconPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        return new Uri($"ms-appdata:///temp/{CacheFolderName}/{iconFileName}");
    }
}
