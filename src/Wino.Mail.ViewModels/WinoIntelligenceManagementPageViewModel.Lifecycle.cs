using System;
using System.Threading.Tasks;
using Wino.Core.Domain;

namespace Wino.Mail.ViewModels;

public partial class WinoIntelligenceManagementPageViewModel
{
    private readonly object _acceptedWorkGate = new();
    private Task _acceptedWork = Task.CompletedTask;
    private bool _acceptMessageWork;

    public bool HasPendingAcceptedWork { get { lock (_acceptedWorkGate) return !_acceptedWork.IsCompleted; } }
    public async Task DrainAcceptedWorkAsync()
    {
        while (true)
        {
            Task work;
            lock (_acceptedWorkGate) work = _acceptedWork;
            await work.ConfigureAwait(false);
            lock (_acceptedWorkGate) { if (ReferenceEquals(work, _acceptedWork)) return; }
        }
    }

    private void TrackAcceptedWork(Func<Task> operation)
    {
        lock (_acceptedWorkGate)
        {
            if (!_acceptMessageWork) return;
            var work = ObserveAcceptedWorkAsync(operation);
            _acceptedWork = _acceptedWork.IsCompleted ? work : Task.WhenAll(_acceptedWork, work);
        }
    }

    private async Task ObserveAcceptedWorkAsync(Func<Task> operation)
    {
        try { await operation().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            await ExecuteUIThread(() => ApplyErrorStatus(Translator.WinoAccount_Management_LoadFailed));
        }
    }
}
