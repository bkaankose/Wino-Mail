using System;

namespace Wino.Core.Domain.Models.Calendar;

public sealed class CalDavConnectionSettings
{
    public Uri ServiceUri { get; init; }
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}

