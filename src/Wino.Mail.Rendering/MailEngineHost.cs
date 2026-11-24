using System;
using System.Threading;
using Serilog;
using Wino.Renderer;
using Wino.Renderer.Platform;
using Wino.Renderer.Primitives;

namespace Wino.Mail.Rendering;

/// <summary>
/// Owns the one platform backend and the one mail engine of the process. Every reader and composer view shares them.
/// The head supplies the backend through its <see cref="IMailEngineBackendFactory"/>.
/// </summary>
public sealed partial class MailEngineHost : IDisposable
{
    private readonly IMailEngineBackendFactory _backendFactory;
    private readonly Lock _gate = new();
    private IPlatformBackend? _backend;
    private MailEngine? _engine;
    private bool _disposed;

    public MailEngineHost(IMailEngineBackendFactory backendFactory)
    {
        _backendFactory = backendFactory;
    }

    public MailEngine Engine
    {
        get
        {
            EnsureCreated();
            return _engine ?? throw new ObjectDisposedException(nameof(MailEngineHost));
        }
    }

    public IPlatformBackend Backend
    {
        get
        {
            EnsureCreated();
            return _backend ?? throw new ObjectDisposedException(nameof(MailEngineHost));
        }
    }

    public void Trim(MemoryTrimLevel level)
    {
        lock (_gate)
        {
            _engine?.Trim(level);
        }
    }

    private void EnsureCreated()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_engine is not null) return;

            var backend = _backendFactory.Create();
            try
            {
                _engine = new MailEngine(backend, new MailEngineOptions { Diagnostics = new SerilogEngineDiagnostics() });
                _backend = backend;
                Log.Information("Mail engine started: {Backend}", _backendFactory.Describe(backend));
            }
            catch
            {
                (backend as IDisposable)?.Dispose();
                throw;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            // The engine does not own the backend, so it goes first.
            _engine?.Dispose();
            _engine = null;
            (_backend as IDisposable)?.Dispose();
            _backend = null;
        }
    }

    private sealed class SerilogEngineDiagnostics : IEngineDiagnostics
    {
        public void Count(string name, long value = 1) { }

        public void Time(string stage, double milliseconds) { }

        public void Report(DiagnosticSeverity severity, string code, string message)
        {
            switch (severity)
            {
                case DiagnosticSeverity.Fault:
                    Log.Error("Mail engine fault {Code}: {Message}", code, message);
                    break;
                case DiagnosticSeverity.LimitHit:
                    Log.Warning("Mail engine limit {Code}: {Message}", code, message);
                    break;
                default:
                    Log.Debug("Mail engine {Severity} {Code}: {Message}", severity, code, message);
                    break;
            }
        }
    }
}
