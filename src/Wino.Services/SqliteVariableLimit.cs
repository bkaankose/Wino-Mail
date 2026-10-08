using System;
using System.Collections.Generic;
using System.Linq;

namespace Wino.Services;

/// <summary>
/// Guards raw SQLite queries against SQLITE_LIMIT_VARIABLE_NUMBER ("too many SQL variables").
/// Queries that build an IN-list from an unbounded collection (a whole mailbox, a sync batch,
/// a select-all) exceed SQLite's per-statement bound-parameter cap and fail in SQLite3.Prepare2.
/// Keep every chunk well under the limit so fixed parameters next to the list still fit.
/// </summary>
internal static class SqliteVariableLimit
{
    /// <summary>
    /// SQLite allows 999 bound variables per statement by default. Stay well below that
    /// so queries combining a list with fixed parameters never approach the cap.
    /// Matches the chunk size already used by the contact and folder batch queries.
    /// </summary>
    public const int MaxVariablesPerQuery = 400;

    /// <summary>
    /// Splits <paramref name="values"/> into chunks whose bound-parameter cost fits the cap.
    /// </summary>
    /// <param name="values">The items backing an IN-list.</param>
    /// <param name="parametersPerItem">Bound parameters each item contributes (2 when two parallel IN-lists share the chunk).</param>
    /// <param name="fixedParameters">Bound parameters outside the list in the same statement.</param>
    public static IEnumerable<T[]> Batch<T>(IEnumerable<T> values, int parametersPerItem = 1, int fixedParameters = 0)
    {
        if (values is null)
            yield break;

        var perItem = Math.Max(1, parametersPerItem);
        var size = Math.Max(1, (MaxVariablesPerQuery - Math.Max(0, fixedParameters)) / perItem);

        foreach (var chunk in values.Chunk(size))
        {
            yield return chunk;
        }
    }
}
