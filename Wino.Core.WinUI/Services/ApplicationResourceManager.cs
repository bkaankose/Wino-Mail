using System.Linq;
using Microsoft.UI.Xaml;
using Wino.Core.Domain.Interfaces;
using Wino.Core.WinUI;

namespace Wino.Services;

public class ApplicationResourceManager : IApplicationResourceManager<ResourceDictionary>
{
    public void AddResource(ResourceDictionary resource)
        => WinoApplication.Current.Resources.MergedDictionaries.Add(resource);
    public void RemoveResource(ResourceDictionary resource)
        => WinoApplication.Current.Resources.MergedDictionaries.Remove(resource);

    public bool ContainsResourceKey(string resourceKey)
        => WinoApplication.Current.Resources.ContainsKey(resourceKey);

    public ResourceDictionary GetLastResource()
        => WinoApplication.Current.Resources.MergedDictionaries.LastOrDefault();

    public void ReplaceResource(string resourceKey, object resource)
        => WinoApplication.Current.Resources[resourceKey] = resource;

    public TReturnType GetResource<TReturnType>(string resourceKey)
        => (TReturnType)WinoApplication.Current.Resources[resourceKey];
}
