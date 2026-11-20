using Wino.Core.Domain.Models.Common;
using Wino.Mail.Linux.Views.Dialogs;

namespace Wino.Mail.Linux.Infrastructure;

// File and folder pickers. Dismissed pickers answer with the empty values the Windows contract uses.
internal sealed partial class GtkDialogService
{
    public async Task<List<SharedFile>> PickFilesAsync(params object[] typeFilters)
    {
        var files = new List<SharedFile>();
        foreach (var path in await PickPathsAsync(true, typeFilters))
        {
            if (await ReadAsync(path) is { } data) files.Add(new SharedFile(path, data));
        }
        return files;
    }

    public async Task<List<PickedFileMetadata>> PickFilesMetadataAsync(params object[] typeFilters)
    {
        var files = new List<PickedFileMetadata>();
        foreach (var path in await PickPathsAsync(true, typeFilters))
        {
            try { files.Add(new PickedFileMetadata(path, new FileInfo(path).Length)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Serilog.Log.Warning(exception, "A picked file could not be read and was skipped.");
            }
        }
        return files;
    }

    public async Task<byte[]> PickWindowsFileContentAsync(params object[] typeFilters)
        => await PickPathsAsync(false, typeFilters) is [var path, ..] && await ReadAsync(path) is { } data ? data : [];

    public async Task<string> PickFilePathAsync(string saveFileName)
        => await PresentAsync(owner => FilePickers.SaveAsync(owner, saveFileName ?? string.Empty), null) ?? string.Empty;

    public async Task<string> PickWindowsFolderAsync()
        => await PresentAsync(FilePickers.SelectFolderAsync, null) ?? string.Empty;

    private Task<string[]> PickPathsAsync(bool multiple, object[] typeFilters)
    {
        var extensions = FilePickers.Extensions(typeFilters ?? []);
        return PresentAsync(owner => FilePickers.OpenAsync(owner, multiple, extensions), Array.Empty<string>());
    }

    private static async Task<byte[]?> ReadAsync(string path)
    {
        try { return await File.ReadAllBytesAsync(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(exception, "A picked file could not be read and was skipped.");
            return null;
        }
    }
}
