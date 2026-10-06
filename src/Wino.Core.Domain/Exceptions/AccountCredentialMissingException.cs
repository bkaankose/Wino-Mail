using System;

namespace Wino.Core.Domain.Exceptions;

/// <summary>A referenced credential no longer exists; explicit retrieval cannot supply a usable secret.</summary>
public class AccountCredentialMissingException()
    : Exception("A referenced credential is missing. Reauthentication or explicit recovery is required.");
