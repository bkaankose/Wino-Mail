using System.Linq;
using Microsoft.UI.Xaml;
using MailCal.Core.Domain.Interfaces;
using MailCal.Mail.WinUI;

namespace MailCal.Services;

public class ApplicationResourceManager : IApplicationResourceManager<ResourceDictionary>
{
    public void AddResource(ResourceDictionary resource)
        => MailCalApplication.Current.Resources.MergedDictionaries.Add(resource);
    public void RemoveResource(ResourceDictionary resource)
        => MailCalApplication.Current.Resources.MergedDictionaries.Remove(resource);

    public bool ContainsResourceKey(string resourceKey)
        => MailCalApplication.Current.Resources.ContainsKey(resourceKey);

    public ResourceDictionary GetLastResource()
        => MailCalApplication.Current.Resources.MergedDictionaries.LastOrDefault()!;

    public void ReplaceResource(string resourceKey, object resource)
        => MailCalApplication.Current.Resources[resourceKey] = resource;

    public TReturnType GetResource<TReturnType>(string resourceKey)
        => (TReturnType)MailCalApplication.Current.Resources[resourceKey];
}
