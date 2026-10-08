using System;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

public interface IDispatcher
{
    Task ExecuteOnUIThread(Action action);
}
