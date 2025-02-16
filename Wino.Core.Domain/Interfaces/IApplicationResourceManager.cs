namespace Wino.Core.Domain.Interfaces
{
    public interface IApplicationResourceManager<T>
    {
        void RemoveResource(T resource);
        void AddResource(T resource);
        bool ContainsResourceKey(string resourceKey);
        void ReplaceResource(string resourceKey, object resource);
        T GetLastResource();
        TReturnType GetResource<TReturnType>(string resourceKey);
    }
}
