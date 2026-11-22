using System.Windows.Input;
using Wino.Platform.Linux.Security;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Runs a setup command only after the new installation's protection key is in the keyring, so the system's keyring
/// prompt appears in answer to the user's click. A dismissed prompt cancels the command.
/// </summary>
internal sealed class KeyringGatedCommand : ICommand
{
    private readonly ICommand _inner;
    private readonly LinuxProtectionKeySetup _keySetup;
    private bool _waiting;

    public KeyringGatedCommand(ICommand inner, LinuxProtectionKeySetup keySetup)
    {
        _inner = inner;
        _keySetup = keySetup;
        _inner.CanExecuteChanged += (_, args) => CanExecuteChanged?.Invoke(this, args);
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_waiting && _inner.CanExecute(parameter);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        SetWaiting(true);
        bool ready;
        try { ready = await _keySetup.EnsureKeyAsync(); }
        finally { SetWaiting(false); }
        if (ready && _inner.CanExecute(parameter)) _inner.Execute(parameter);
    }

    private void SetWaiting(bool waiting)
    {
        _waiting = waiting;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
