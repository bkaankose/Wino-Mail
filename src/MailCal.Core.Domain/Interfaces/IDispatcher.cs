using System;
using System.Threading.Tasks;

namespace MailCal.Core.Domain.Interfaces;

public interface IDispatcher
{
    Task ExecuteOnUIThread(Action action);
}
