using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

/// <summary>
/// Atomic nonsecret preferences in "preferences.json" under the given folder (the application data root).
/// Roaming preferences remain local until a roaming backend exists. Nullable annotations follow
/// <see cref="IConfigurationService"/>, whose defaults are unconstrained generic values.
/// </summary>
public sealed class JsonFileConfigurationService : IConfigurationService
{
    private readonly string _directory;
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, string?> _values;

    public JsonFileConfigurationService(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _path = Path.Combine(_directory, "preferences.json");
        _values = File.Exists(_path)
            ? JsonSerializer.Deserialize(File.ReadAllBytes(_path), PreferencesFileJsonContext.Default.DictionaryStringString) ?? new(StringComparer.Ordinal)
            : new(StringComparer.Ordinal);
    }

    public bool Contains(string key) { lock (_gate) return _values.ContainsKey(key); }
    public bool Remove(string key)
    {
        lock (_gate)
        {
            if (!_values.ContainsKey(key)) return false;
            var updated = new Dictionary<string, string?>(_values, StringComparer.Ordinal);
            updated.Remove(key);
            Save(updated);
            _values = updated;
            return true;
        }
    }

    public void Set(string key, object value) => SetInternal(key, value);
    public void SetRoaming(string key, object value) => SetInternal("roaming:" + key, value);
    public T Get<T>(string key, T defaultValue = default) => GetInternal(key, defaultValue);
    public T GetRoaming<T>(string key, T defaultValue = default) => GetInternal("roaming:" + key, defaultValue);

    private void SetInternal(string key, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var text = value is IFormattable formattable ? formattable.ToString(null, CultureInfo.InvariantCulture) : value?.ToString();
        lock (_gate)
        {
            var updated = new Dictionary<string, string?>(_values, StringComparer.Ordinal) { [key] = text };
            Save(updated);
            _values = updated;
        }
    }

    private T GetInternal<T>(string key, T defaultValue)
    {
        lock (_gate)
        {
            if (!_values.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text)) return defaultValue;
            var type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            try
            {
                object value = type.IsEnum ? Enum.Parse(type, text) :
                    type == typeof(Guid) ? Guid.Parse(text) :
                    type == typeof(TimeSpan) ? TimeSpan.Parse(text, CultureInfo.InvariantCulture) :
                    Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
                return (T)value;
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidCastException or OverflowException)
            { return defaultValue; }
        }
    }

    private void Save(Dictionary<string, string?> values)
    {
        Directory.CreateDirectory(_directory);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(values, PreferencesFileJsonContext.Default.DictionaryStringString));
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class PreferencesFileJsonContext : JsonSerializerContext;
