using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wino.Providers.Gmail.Google.Apis.Util;

/// <summary>
/// Source-compatible collection used by the old Google SDK call sites. The REST client
/// serializes every item as a repeated query parameter and does not use reflection.
/// </summary>
public sealed class Repeatable<T> : List<T>
{
    public Repeatable(IEnumerable<T> values) : base(values)
    {
    }
}
