using System;
using Windows.Foundation.Collections;
using Windows.Storage;
using Wino.Core.Domain.Interfaces;

namespace Wino.BackgroundService.Services;

/// <summary>
/// Same ApplicationData.LocalSettings-backed store the UI process uses; both apps live in
/// one MSIX package, so preferences written by either process are visible to the other.
/// </summary>
public class CompanionConfigurationService : IConfigurationService
{
    public bool Contains(string key)
        => ApplicationData.Current.LocalSettings.Values.ContainsKey(key);

    public bool Remove(string key)
        => ApplicationData.Current.LocalSettings.Values.Remove(key);

    public T Get<T>(string key, T defaultValue = default!)
        => GetInternal(key, ApplicationData.Current.LocalSettings.Values, defaultValue);

    public T GetRoaming<T>(string key, T defaultValue = default!)
        => GetInternal(key, ApplicationData.Current.RoamingSettings.Values, defaultValue);

    public void Set(string key, object value)
        => SetInternal(key, value, ApplicationData.Current.LocalSettings.Values);

    public void SetRoaming(string key, object value)
        => SetInternal(key, value, ApplicationData.Current.RoamingSettings.Values);

    private static T GetInternal<T>(string key, IPropertySet collection, T defaultValue = default!)
    {
        if (collection.TryGetValue(key, out object? value))
        {
            var stringValue = value?.ToString();
            if (string.IsNullOrWhiteSpace(stringValue))
                return defaultValue;

            if (typeof(T).IsEnum)
                return (T)Enum.Parse(typeof(T), stringValue);

            if ((typeof(T) == typeof(Guid?) || typeof(T) == typeof(Guid)) && Guid.TryParse(stringValue, out Guid guidResult))
            {
                return (T)(object)guidResult;
            }

            if (typeof(T) == typeof(TimeSpan))
            {
                return (T)(object)TimeSpan.Parse(stringValue);
            }

            var converted = Convert.ChangeType(stringValue, typeof(T));
            return converted is T typed ? typed : defaultValue;
        }

        return defaultValue;
    }

    private static void SetInternal(string key, object value, IPropertySet collection)
        => collection[key] = value?.ToString();
}
