using System;
using System.ComponentModel;
using Windows.Foundation.Collections;
using Windows.Storage;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.UWP.Services
{
    public class ConfigurationService : IConfigurationService
    {
        public T Get<T>(string key, T defaultValue = default)
            => GetInternal(key, ApplicationData.Current.LocalSettings.Values, defaultValue);

        public T GetRoaming<T>(string key, T defaultValue = default)
            => GetInternal(key, ApplicationData.Current.RoamingSettings.Values, defaultValue);

        public void Set(string key, object value)
            => SetInternal(key, value, ApplicationData.Current.LocalSettings.Values);

        public void SetRoaming(string key, object value)
            => SetInternal(key, value, ApplicationData.Current.RoamingSettings.Values);

        private T GetInternal<T>(string key, IPropertySet collection, T defaultValue = default)
        {
            if (collection.ContainsKey(key))
            {
                var value = collection[key]?.ToString();

                if (typeof(T).IsEnum)
                    return (T)Enum.Parse(typeof(T), value);

                if (typeof(T) == typeof(Guid?) && Guid.TryParse(value, out Guid guidResult))
                {
                    return (T)TypeDescriptor.GetConverter(typeof(T)).ConvertFromInvariantString(value);
                }

                return (T)Convert.ChangeType(value, typeof(T));
            }

            return defaultValue;
        }

        private void SetInternal(string key, object value, IPropertySet collection)
            => collection[key] = value?.ToString();
    }
}
