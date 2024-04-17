using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Extensions
{
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

        public static IEnumerable<IBatchChangeRequest> CreateBatch(this IEnumerable<IGrouping<MailSynchronizerOperation, IRequestBase>> items)
        {
            IBatchChangeRequest batch = null;

            foreach (var group in items)
            {
                var key = group.Key;
            }

            yield return batch;
        }

        public static void AddSorted<T>(this List<T> @this, T item) where T : IComparable<T>
        {
            if (@this.Count == 0)
            {
                @this.Add(item);
                return;
            }
            if (@this[@this.Count - 1].CompareTo(item) <= 0)
            {
                @this.Add(item);
                return;
            }
            if (@this[0].CompareTo(item) >= 0)
            {
                @this.Insert(0, item);
                return;
            }
            int index = @this.BinarySearch(item);
            if (index < 0)
                index = ~index;
            @this.Insert(index, item);
        }
    }
}
