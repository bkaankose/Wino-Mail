using System.Linq;
using Windows.UI.Xaml;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services
{
    public class ApplicationResourceManager : IApplicationResourceManager<ResourceDictionary>
    {
        public void AddResource(ResourceDictionary resource)
            => App.Current.Resources.MergedDictionaries.Add(resource);
        public void RemoveResource(ResourceDictionary resource)
            => App.Current.Resources.MergedDictionaries.Remove(resource);

        public bool ContainsResourceKey(string resourceKey)
            => App.Current.Resources.ContainsKey(resourceKey);

        public ResourceDictionary GetLastResource()
            => App.Current.Resources.MergedDictionaries.LastOrDefault();

        public void ReplaceResource(string resourceKey, object resource)
            => App.Current.Resources[resourceKey] = resource;
    }
}
