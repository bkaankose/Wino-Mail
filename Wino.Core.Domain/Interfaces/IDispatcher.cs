using System;
using System.Threading.Tasks;

namespace Wino.Domain.Interfaces
{
    public interface IDispatcher
    {
        Task ExecuteOnUIThread(Action action);
    }
}
