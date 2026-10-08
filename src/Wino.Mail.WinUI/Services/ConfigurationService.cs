using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using Windows.Foundation.Collections;
using Windows.Storage;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.WinUI.Services;

public class ConfigurationService : IConfigurationService
{
    /// <summary>
    /// Converted setting values, keyed by setting name and requested type.
    /// </summary>
    /// <remarks>
    /// Every read otherwise crosses the WinRT boundary into <c>LocalSettings</c> and, for enums,
    /// parses a string. Mail list templates read display, avatar and time format preferences once
    /// per row, so this sits directly on the folder-load path. The cache is static because the
    /// service is registered as transient while the settings themselves are process-wide, and it
    /// is only correct as long as nothing writes these keys outside <see cref="Set"/> and
    /// <see cref="Remove"/>. Activation code writes its own keys, which are never read here.
    /// </remarks>
    private static readonly ConcurrentDictionary<CacheKey, object?> _cache = new();

    /// <summary>Marks a setting that is not present, so its absence is cached too.</summary>
    private static readonly object MissingValue = new();

    public bool Contains(string key)
        => ApplicationData.Current.LocalSettings.Values.ContainsKey(key);

    public bool Remove(string key)
    {
        InvalidateCache(key);
        return ApplicationData.Current.LocalSettings.Values.Remove(key);
    }

    public T Get<T>(string key, T defaultValue = default!)
        => GetCached(key, isRoaming: false, defaultValue);

    public T GetRoaming<T>(string key, T defaultValue = default!)
        => GetCached(key, isRoaming: true, defaultValue);

    public void Set(string key, object value)
    {
        InvalidateCache(key);
        SetInternal(key, value, ApplicationData.Current.LocalSettings.Values);
    }

    public void SetRoaming(string key, object value)
    {
        InvalidateCache(key);
        SetInternal(key, value, ApplicationData.Current.RoamingSettings.Values);
    }

    private static T GetCached<T>(string key, bool isRoaming, T defaultValue)
    {
        var cacheKey = new CacheKey(key, typeof(T), isRoaming);
        if (_cache.TryGetValue(cacheKey, out var cachedValue))
        {
            // An absent setting is cached as the sentinel so the caller's own default wins.
            // Different call sites may pass different defaults for the same key.
            return ReferenceEquals(cachedValue, MissingValue)
                ? defaultValue
                : (T)cachedValue!;
        }

        var collection = isRoaming
            ? ApplicationData.Current.RoamingSettings.Values
            : ApplicationData.Current.LocalSettings.Values;

        if (!TryGetStored<T>(key, collection, out var storedValue))
        {
            _cache[cacheKey] = MissingValue;
            return defaultValue;
        }

        _cache[cacheKey] = storedValue;
        return storedValue;
    }

    private static void InvalidateCache(string key)
    {
        foreach (var cacheKey in _cache.Keys)
        {
            if (string.Equals(cacheKey.Key, key, StringComparison.Ordinal))
            {
                _cache.TryRemove(cacheKey, out _);
            }
        }
    }

    /// <summary>
    /// Reads and converts a stored value. Returns false when the setting is absent, blank, or
    /// cannot be converted, which is the caller's cue to fall back to its own default.
    /// </summary>
    private static bool TryGetStored<T>(string key, IPropertySet collection, out T value)
    {
        value = default!;

        if (!collection.TryGetValue(key, out object? storedValue))
            return false;

        var stringValue = storedValue?.ToString();
        if (string.IsNullOrWhiteSpace(stringValue))
            return false;

        if (typeof(T).IsEnum)
        {
            value = (T)Enum.Parse(typeof(T), stringValue);
            return true;
        }

        if ((typeof(T) == typeof(Guid?) || typeof(T) == typeof(Guid)) && Guid.TryParse(stringValue, out Guid guidResult))
        {
            value = (T)(object)guidResult;
            return true;
        }

        if (typeof(T) == typeof(TimeSpan))
        {
            value = (T)(object)TimeSpan.Parse(stringValue);
            return true;
        }

        var converted = Convert.ChangeType(stringValue, typeof(T));
        if (converted is not T typed)
            return false;

        value = typed;
        return true;
    }

    private static void SetInternal(string key, object value, IPropertySet collection)
        => collection[key] = value?.ToString();

    private readonly record struct CacheKey(string Key, Type Type, bool IsRoaming);
}
