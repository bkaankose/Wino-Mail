#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Attachments;

public sealed record AttachmentFileSource(
    string FileName,
    string? DeclaredMimeType,
    AttachmentFileOrigin Origin,
    Func<CancellationToken, ValueTask<Stream>> OpenReadAsync,
    string? LocalFilePath = null);
