using System.Linq;
using Wino.Domain.Interfaces;


#if NET8_0
using Microsoft.UI.Xaml;
#else
using Microsoft.UI.Xaml;
#endif
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

        public TReturn GetResource<TReturn>(string resourceKey)
            => (TReturn)App.Current.Resources[resourceKey];
    }
}
