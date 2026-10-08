using System;
using System.Collections.Generic;
using System.Linq;

namespace Wino.Core.Extensions;

public static class ListExtensions
{
    public static IEnumerable<T> FlattenBy<T>(this IEnumerable<T> nodes, Func<T, IEnumerable<T>> selector)
    {
        if (nodes.Any() == false)
            return nodes;

        var descendants = nodes
            .SelectMany(selector)
            .FlattenBy(selector);

        return nodes.Concat(descendants);
    }
}
