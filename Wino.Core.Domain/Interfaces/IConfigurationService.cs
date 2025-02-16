namespace Wino.Core.Domain.Interfaces;

public interface IConfigurationService
{
    void Set(string key, object value);
    T Get<T>(string key, T defaultValue = default);

    void SetRoaming(string key, object value);
    T GetRoaming<T>(string key, T defaultValue = default);
}
