using Android.Content;
using AndroidX.Work;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Android.Background;

/// <summary>
/// Schedules the WorkManager flush of queued changes (design: WorkManager only flushes pending changes; no periodic
/// background synchronization). Every queued change request (<see cref="ISynchronizationManager.RequestsQueued"/>)
/// and every move to the background enqueues one unique, expedited, one-time work item that needs a network
/// connection; the head's worker runs <see cref="IApplicationRuntime.FlushQueuedRequestsAsync"/>. A queued change
/// while the work runs appends one more run, so nothing queued after the flush started is left behind.
/// </summary>
public sealed class AndroidPendingChangesScheduler
{
    public const string UniqueWorkName = "wino-flush-queued-changes";

    private readonly Context _context;
    private readonly Type _workerType;
    private readonly ISynchronizationManager _synchronizationManager;

    public AndroidPendingChangesScheduler(Context context, Type workerType, ISynchronizationManager synchronizationManager)
    {
        _context = context;
        _workerType = workerType;
        _synchronizationManager = synchronizationManager;
    }

    private int _started;

    /// <summary>Follows queued change requests until the process ends. Calling it again does nothing.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) _synchronizationManager.RequestsQueued += OnRequestsQueued;
    }

    private void OnRequestsQueued(object? sender, Guid accountId) => Enqueue("queued change");

    /// <summary>Enqueues the flush when any synchronizer still holds queued requests (onStop).</summary>
    public void EnqueueIfPending(string reason)
    {
        try
        {
            if (_synchronizationManager.GetAllSynchronizers().Any(synchronizer => synchronizer.HasQueuedRequests()))
                Enqueue(reason);
        }
        catch (Exception error)
        {
            // The synchronization manager is not initialized before the runtime is.
            Serilog.Log.Debug(error, "Queued changes could not be checked ({Reason}).", reason);
        }
    }

    public void Enqueue(string reason)
    {
        try
        {
            var constraints = new Constraints.Builder()
                .SetRequiredNetworkType(NetworkType.Connected!)
                .Build();
            var request = new OneTimeWorkRequest.Builder(Java.Lang.Class.FromType(_workerType))
                .SetConstraints(constraints)
                .SetExpedited(OutOfQuotaPolicy.RunAsNonExpeditedWorkRequest!)
                .AddTag(UniqueWorkName)
                .Build();
            WorkManager.GetInstance(_context).EnqueueUniqueWork(UniqueWorkName, ExistingWorkPolicy.AppendOrReplace!, (OneTimeWorkRequest)request);
            Serilog.Log.Information("Queued changes flush enqueued ({Reason}).", reason);
        }
        catch (Exception error)
        {
            Serilog.Log.Warning(error, "The queued changes flush could not be enqueued ({Reason}).", reason);
        }
    }
}
