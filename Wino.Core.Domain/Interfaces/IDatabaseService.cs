using SQLite;

namespace Wino.Domain.Interfaces
{
    public interface IDatabaseService : IInitializeAsync
    {
        SQLiteAsyncConnection Connection { get; }
    }
}
