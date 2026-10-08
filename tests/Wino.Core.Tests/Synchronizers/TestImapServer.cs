using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Integration;

namespace Wino.Core.Tests.Synchronizers;

// Real MailKit sockets with deterministic command barriers; no provider credentials.
internal sealed class TestImapServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<TcpClient> _sockets = [];
    private readonly ConcurrentBag<Task> _handlers = [];
    private readonly Task _accept;
    private int _accepted;
    private int _noops;
    private int _active;
    private int _rejected;
    public bool HoldGreeting { get; init; }
    public bool HoldFirstNoop { get; init; }
    public bool ResetFirstNoop { get; init; }
    public bool SupportsIdle { get; init; }
    public bool RejectIdle { get; init; }
    public TimeSpan? IdleTimeout { get; init; }
    public int MaxConnections { get; init; } = int.MaxValue;
    public int RejectedConnections => Volatile.Read(ref _rejected);
    public TaskCompletionSource IdleClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseGreeting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource NoopReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseNoop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource IdleReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource DoneReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentQueue<string> Commands { get; } = new();
    public Channel<string> IdleUpdates { get; } = Channel.CreateUnbounded<string>();
    public int ConnectionCount => Volatile.Read(ref _accepted);
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public TestImapServer()
    {
        _listener.Start();
        _accept = AcceptAsync();
    }

    public CustomServerInformation Settings(int max = 1) => new()
    {
        IncomingServer = "127.0.0.1", IncomingServerPort = Port.ToString(),
        IncomingServerSocketOption = ImapConnectionSecurity.None,
        IncomingAuthenticationMethod = ImapAuthenticationMethod.None,
        ConnectionPolicyVersion = ImapConnectionPolicyVersion.Corrected, MaxConcurrentClients = max
    };

    public ImapClientPool CreatePool(int max = 1, bool test = false) => new(test
        ? ImapClientPoolOptions.CreateTestPool(Settings(max))
        : ImapClientPoolOptions.CreateDefault(Settings(max)));

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                _sockets.Add(socket);
                Interlocked.Increment(ref _accepted);
                Accepted.TrySetResult();
                _handlers.Add(ServeAsync(socket));
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ServeAsync(TcpClient socket)
    {
        var active = Interlocked.Increment(ref _active);
        try
        {
            if (HoldGreeting) await ReleaseGreeting.Task.WaitAsync(_stop.Token);
            using var stream = socket.GetStream();
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\r\n" };
            if (active > MaxConnections)
            {
                Interlocked.Increment(ref _rejected);
                await writer.WriteLineAsync("* BYE connection limit exceeded");
                return;
            }
            var capabilities = "IMAP4rev1" + (SupportsIdle ? " IDLE" : "");
            await writer.WriteLineAsync($"* PREAUTH [CAPABILITY {capabilities}] loopback test server");
            while (!_stop.IsCancellationRequested)
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                if (IdleTimeout.HasValue) readTimeout.CancelAfter(IdleTimeout.Value);
                var line = await reader.ReadLineAsync(readTimeout.Token);
                if (line == null) break;
                var parts = line.Split(' ', 3);
                var tag = parts[0];
                var verb = parts[1];
                Commands.Enqueue(verb);
                if (verb == "CAPABILITY") await writer.WriteLineAsync("* CAPABILITY " + capabilities);
                if (verb == "LIST") await writer.WriteLineAsync("* LIST () \"/\" \"INBOX\"");
                if (verb == "NOOP" && Interlocked.Increment(ref _noops) == 1)
                {
                    NoopReceived.TrySetResult();
                    if (ResetFirstNoop)
                    {
                        socket.Client.LingerState = new LingerOption(true, 0);
                        socket.Close();
                        return;
                    }
                    if (HoldFirstNoop) await ReleaseNoop.Task.WaitAsync(_stop.Token);
                }
                if (verb is "EXAMINE" or "SELECT")
                {
                    await writer.WriteLineAsync("* FLAGS (\\Seen)");
                    await writer.WriteLineAsync("* 0 EXISTS");
                    await writer.WriteLineAsync("* OK [UIDVALIDITY 1]");
                }
                if (verb == "IDLE")
                {
                    IdleReceived.TrySetResult();
                    if (RejectIdle)
                    {
                        await writer.WriteLineAsync(tag + " BAD unsupported");
                        continue;
                    }
                    await writer.WriteLineAsync("+ idling");
                    var doneRead = reader.ReadLineAsync(_stop.Token).AsTask();
                    while (!doneRead.IsCompleted)
                    {
                        var updates = IdleUpdates.Reader.WaitToReadAsync(_stop.Token).AsTask();
                        if (await Task.WhenAny(doneRead, updates) == doneRead) break;
                        while (IdleUpdates.Reader.TryRead(out var update)) await writer.WriteLineAsync(update);
                    }
                    var done = await doneRead;
                    if (done == "DONE")
                    {
                        Commands.Enqueue(done);
                        DoneReceived.TrySetResult();
                    }
                }
                await writer.WriteLineAsync(tag + " OK " + verb);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            if (!_stop.IsCancellationRequested && IdleTimeout.HasValue) IdleClosed.TrySetResult();
        }
        finally
        {
            socket.Dispose();
            Interlocked.Decrement(ref _active);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        foreach (var socket in _sockets) socket.Dispose();
        await _accept;
        await Task.WhenAll(_handlers);
        _stop.Dispose();
    }
}
