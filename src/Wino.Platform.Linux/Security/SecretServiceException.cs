namespace Wino.Platform.Linux.Security;

/// <summary>The Secret Service did not answer, is locked, or refused the item. The app never continues without it.</summary>
public sealed class SecretServiceException(string message) : Exception(message);
