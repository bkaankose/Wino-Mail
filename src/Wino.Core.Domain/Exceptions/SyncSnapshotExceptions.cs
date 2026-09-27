using System;

namespace Wino.Core.Domain.Exceptions;

/// <summary>
/// The snapshot could not be opened: wrong secret, a previous password, or a damaged file.
/// </summary>
public sealed class SyncSnapshotDecryptionException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// The bytes are not a Wino sync snapshot, or use a format this build does not understand.
/// </summary>
public sealed class SyncSnapshotInvalidFileException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// No key was available and the user did not supply the secret.
/// </summary>
public sealed class SyncSnapshotKeyRequiredException(string message) : Exception(message);
