#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Attachments;

namespace Wino.Services;

internal sealed class AttachmentFileService(
    IContentTypeDetectionService contentTypeDetectionService,
    IExternalLauncher externalLauncher,
    IAttachmentPlatformService attachmentPlatformService,
    IWinoLogger logger) : IAttachmentFileService
{
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    public async ValueTask<ContentTypeDetectionResult> InspectAsync(
        AttachmentFileSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        try
        {
            await using var content = await source.OpenReadAsync(cancellationToken).ConfigureAwait(false);
            return await contentTypeDetectionService
                .DetectAsync(content, source.FileName, source.DeclaredMimeType, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.CaptureException(ex, "AttachmentContentTypeInspection");
            return new ContentTypeDetectionResult(ContentTypeDetectionStatus.Failed);
        }
    }

    public async Task<AttachmentFileOperationResult> OpenAsync(
        AttachmentFileSource source,
        string workingFolderPath,
        ContentTypeDetectionResult? knownDetection,
        bool mismatchApproved,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        try
        {
            var detection = knownDetection ?? await InspectAsync(source, cancellationToken);
            if (detection.RequiresOpenConfirmation && !mismatchApproved)
            {
                return new AttachmentFileOperationResult(
                    AttachmentFileOperationStatus.ConfirmationRequired,
                    Detection: detection);
            }

            if (source.Origin == AttachmentFileOrigin.Local &&
                !string.IsNullOrWhiteSpace(source.LocalFilePath) &&
                File.Exists(source.LocalFilePath))
            {
                var launch = await externalLauncher.LaunchFileAsync(source.LocalFilePath, cancellationToken);
                return new AttachmentFileOperationResult(
                    MapLaunchStatus(launch.Status),
                    source.LocalFilePath,
                    detection,
                    launch.ErrorMessage);
            }

            var materializedPath = await MaterializeReceivedFileAsync(source, workingFolderPath, cancellationToken);
            var opened = await attachmentPlatformService.OpenReceivedAsync(materializedPath, cancellationToken);
            return opened with { FilePath = materializedPath, Detection = detection };
        }
        catch (OperationCanceledException)
        {
            return new AttachmentFileOperationResult(AttachmentFileOperationStatus.Cancelled);
        }
        catch (Exception ex)
        {
            logger.CaptureException(ex, "AttachmentFileOpen");
            return new AttachmentFileOperationResult(AttachmentFileOperationStatus.Failed, ErrorMessage: ex.Message);
        }
    }

    public async Task<AttachmentFileOperationResult> SaveAsync(
        AttachmentFileSource source,
        string destinationFolderPath,
        string? destinationFileName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        try
        {
            var destinationFolder = Path.GetFullPath(destinationFolderPath);
            Directory.CreateDirectory(destinationFolder);
            var destination = Path.GetFullPath(Path.Combine(
                destinationFolder,
                SanitizeFileName(destinationFileName ?? source.FileName)));
            var expectedPrefix = destinationFolder.EndsWith(Path.DirectorySeparatorChar)
                ? destinationFolder
                : destinationFolder + Path.DirectorySeparatorChar;

            if (!destination.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The attachment filename resolves outside its destination directory.");

            if (source.Origin == AttachmentFileOrigin.Local &&
                !string.IsNullOrWhiteSpace(source.LocalFilePath) &&
                File.Exists(source.LocalFilePath))
            {
                File.Copy(source.LocalFilePath, destination, overwrite: true);
            }
            else
            {
                await using var input = await source.OpenReadAsync(cancellationToken);
                await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    await input.CopyToAsync(output, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }

                var policy = await attachmentPlatformService.ApplySavePolicyAsync(destination, cancellationToken);
                if (!policy.IsSuccess)
                    return policy with { FilePath = destination };
            }

            return new AttachmentFileOperationResult(AttachmentFileOperationStatus.Succeeded, destination);
        }
        catch (OperationCanceledException)
        {
            return new AttachmentFileOperationResult(AttachmentFileOperationStatus.Cancelled);
        }
        catch (Exception ex)
        {
            logger.CaptureException(ex, "AttachmentFileSave");
            return new AttachmentFileOperationResult(AttachmentFileOperationStatus.Failed, ErrorMessage: ex.Message);
        }
    }

    private static AttachmentFileOperationStatus MapLaunchStatus(PlatformOperationStatus status)
        => status switch
        {
            PlatformOperationStatus.Succeeded => AttachmentFileOperationStatus.Succeeded,
            PlatformOperationStatus.Cancelled => AttachmentFileOperationStatus.Cancelled,
            PlatformOperationStatus.Unavailable => AttachmentFileOperationStatus.Unavailable,
            _ => AttachmentFileOperationStatus.Failed
        };

    internal static string SanitizeFileName(string? fileName)
    {
        var candidate = Path.GetFileName(fileName ?? string.Empty).Trim().TrimEnd('.', ' ');
        var invalidCharacters = Path.GetInvalidFileNameChars();
        candidate = new string(candidate.Select(character =>
            invalidCharacters.Contains(character) || character == ':' ? '_' : character).ToArray());

        if (string.IsNullOrWhiteSpace(candidate))
            candidate = "attachment.bin";

        var stem = Path.GetFileNameWithoutExtension(candidate);
        if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
            candidate = $"_{candidate}";

        return candidate;
    }

    private static async Task<string> MaterializeReceivedFileAsync(
        AttachmentFileSource source,
        string workingFolderPath,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(workingFolderPath);
        Directory.CreateDirectory(root);

        var operationFolder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(operationFolder);

        var destination = Path.GetFullPath(Path.Combine(operationFolder, SanitizeFileName(source.FileName)));
        var expectedPrefix = operationFolder.EndsWith(Path.DirectorySeparatorChar)
            ? operationFolder
            : operationFolder + Path.DirectorySeparatorChar;

        if (!destination.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The attachment filename resolves outside its working directory.");

        try
        {
            await using var input = await source.OpenReadAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);

            return destination;
        }
        catch
        {
            // Only this operation owns this newly created directory. Successful launches
            // retain their files for the external application and host temp cleanup.
            try
            {
                Directory.Delete(operationFolder, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            throw;
        }
    }
}
