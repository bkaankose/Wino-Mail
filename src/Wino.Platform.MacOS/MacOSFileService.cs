using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Services;

namespace Wino.Platform.MacOS;

[SupportedOSPlatform("macos")]
public sealed class MacOSFileService(IApplicationResourceResolver resources) : IFileService
{
    public async Task<string> CopyFileAsync(string sourceFilePath, string destinationFolderPath)
    {
        Directory.CreateDirectory(destinationFolderPath);
        var fileName = Path.GetFileName(sourceFilePath);
        var attempt = 0;
        while (true)
        {
            var name = attempt == 0 ? fileName : $"{Path.GetFileNameWithoutExtension(fileName)} ({attempt}){Path.GetExtension(fileName)}";
            var destination = Path.Combine(destinationFolderPath, name);
            FileStream output;
            try { output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) when (File.Exists(destination)) { attempt++; continue; }
            try
            {
                await using (output)
                await using (var input = File.OpenRead(sourceFilePath))
                    await input.CopyToAsync(output).ConfigureAwait(false);
                return destination;
            }
            catch { File.Delete(destination); throw; }
        }
    }

    public Task<Stream> GetFileStreamAsync(string folderPath, string fileName)
    {
        Directory.CreateDirectory(folderPath);
        return Task.FromResult<Stream>(File.Open(Path.Combine(folderPath, fileName), FileMode.Create, FileAccess.Write, FileShare.None));
    }

    public Task<string> GetFileContentByApplicationUriAsync(string resourcePath)
    {
        var uri = new Uri(resourcePath, UriKind.RelativeOrAbsolute);
        Uri resolved;
        if (!uri.IsAbsoluteUri) resolved = resources.ResolvePackagedResource(resourcePath);
        else if (uri.Scheme == "ms-appx") resolved = resources.ResolvePackagedResource(Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/'));
        else if (uri.Scheme == "ms-appdata" && uri.AbsolutePath.StartsWith("/local/", StringComparison.Ordinal))
            resolved = resources.ResolveLocalResource(Uri.UnescapeDataString(uri.AbsolutePath[7..]));
        else if (uri.IsFile) resolved = uri;
        else throw new NotSupportedException("Unsupported application resource URI.");
        return File.ReadAllTextAsync(resolved.LocalPath);
    }

    public async Task<bool> SaveLogsToFolderAsync(string logsFolder, string destinationFolder)
        => !string.IsNullOrEmpty(await CreateLogsArchiveAsync(logsFolder, destinationFolder, Constants.LogArchiveFileName).ConfigureAwait(false));

    public async Task<string> CreateLogsArchiveAsync(string logsFolder, string destinationFolder, string archiveFileName, bool sanitizeSensitiveData = false)
    {
        if (!Directory.Exists(logsFolder)) return string.Empty;
        var files = Directory.GetFiles(logsFolder, "*.log");
        if (files.Length == 0) return string.Empty;
        using var output = await GetFileStreamAsync(destinationFolder, archiveFileName).ConfigureAwait(false);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, true);
        foreach (var path in files)
        {
            using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var entry = archive.CreateEntry(Path.GetFileName(path), CompressionLevel.Fastest).Open();
            if (!sanitizeSensitiveData) { await input.CopyToAsync(entry).ConfigureAwait(false); continue; }
            using var reader = new StreamReader(input);
            using var writer = new StreamWriter(entry, leaveOpen: true);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                await writer.WriteLineAsync(DiagnosticLogRedactor.Redact(line)).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        return Path.Combine(destinationFolder, archiveFileName);
    }
}
