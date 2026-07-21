namespace Wino.Companion;

internal sealed class CompanionSingleInstance : IDisposable
{
    private const string MutexName = "Local\\Wino.Companion.Singleton.v2";
    private const string ActivationEventName = "Local\\Wino.Companion.Activate.v2";

    private readonly Mutex mutex;
    private readonly EventWaitHandle activationEvent;
    private RegisteredWaitHandle? registeredWait;

    private CompanionSingleInstance(Mutex mutex, EventWaitHandle activationEvent, bool isPrimary)
    {
        this.mutex = mutex;
        this.activationEvent = activationEvent;
        IsPrimary = isPrimary;
    }

    public bool IsPrimary { get; }

    public event EventHandler? ActivationRequested;

    public static CompanionSingleInstance Acquire()
    {
        var mutex = new Mutex(true, MutexName, out var isPrimary);
        var activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        var instance = new CompanionSingleInstance(mutex, activationEvent, isPrimary);
        if (isPrimary)
        {
            instance.registeredWait = ThreadPool.RegisterWaitForSingleObject(
                activationEvent,
                static (state, _) => ((CompanionSingleInstance)state!).ActivationRequested?.Invoke(state, EventArgs.Empty),
                instance,
                Timeout.Infinite,
                false);
        }

        return instance;
    }

    public void SignalPrimary() => activationEvent.Set();

    public void Dispose()
    {
        registeredWait?.Unregister(null);
        activationEvent.Dispose();
        if (IsPrimary)
        {
            mutex.ReleaseMutex();
        }

        mutex.Dispose();
    }
}
