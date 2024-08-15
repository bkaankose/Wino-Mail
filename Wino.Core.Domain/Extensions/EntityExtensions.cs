using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Entities;

namespace Wino.Core.Domain.Extensions
{
    public static class EntityExtensions
    {
        public static List<MailAccountAlias> GetFinalAliasList(List<MailAccountAlias> localAliases, List<MailAccountAlias> networkAliases)
        {
            var finalAliases = new List<MailAccountAlias>();

            var networkAliasDict = networkAliases.ToDictionary(a => a, a => a);

            // Handle updating and retaining existing aliases
            foreach (var localAlias in localAliases)
            {
                if (networkAliasDict.TryGetValue(localAlias, out var networkAlias))
                {
                    // If alias exists in both lists, update it with the network alias (preserving Id from local)
                    networkAlias.Id = localAlias.Id; // Preserve the local Id
                    finalAliases.Add(networkAlias);
                    networkAliasDict.Remove(localAlias); // Remove from dictionary to track what's been handled
                }
                // If the alias isn't in the network list, it's considered deleted and not added to finalAliases
            }

            // Add new aliases that were not in the local list
            finalAliases.AddRange(networkAliasDict.Values);

            return finalAliases;
        }
    }
}
