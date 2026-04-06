using System;

namespace Wino.Core.Domain.Misc;

public static class MessageIdGenerator
{
    private const string Domain = "wino-mail.app";

    public static string Generate()
    {
        return $"<{Guid.NewGuid()}@{Domain}>";
    }
}
