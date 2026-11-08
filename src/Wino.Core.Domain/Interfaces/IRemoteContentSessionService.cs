using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Per-message remote content choices made in the reader. Kept in memory until the app closes, never saved.
/// </summary>
public interface IRemoteContentSessionService
{
    void Set(Guid id, RemoteContentMode mode);
    bool TryGet(Guid id, out RemoteContentMode mode);
    void Clear(Guid id);

    /// <summary>
    /// A stable id for a message that has no <c>MailCopy</c>, such as an opened EML file.
    /// </summary>
    Guid GetFileId(string filePath);
}
