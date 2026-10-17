using System.Net;
using System.Text.Json;
using Serilog;
using Serilog.Events;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;
using Wino.PushRelay.Socket;
using Wino.PushRelay.Socket.Transport;
using Wino.Services.PushRelay;

namespace Wino.Relay.ConsoleApp;

internal static class Program
{
    private const string LocalApiUrl = "https://localhost:7204/";
    private const string ProductionApiUrl = "https://api.winomail.app/";

    public static int Main(string[] args)
    {
        // The macOS app host gives the main thread an AppKit synchronization context whose run loop a console never pumps,
        // so every await would park forever. The whole console therefore runs on a thread-pool thread.
        return Task.Run(() => MainAsync(args)).GetAwaiter().GetResult();
    }

    private static async Task<int> MainAsync(string[] args)
    {
        if (!Arguments.TryParse(args, out var arguments, out var error))
        {
            ConsoleOutput.Error(error ?? "Invalid arguments.");
            Arguments.PrintHelp();
            return 2;
        }

        if (arguments.ShowHelp)
        {
            Arguments.PrintHelp();
            return 0;
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Verbose = arguments.Verbose;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(new ConsoleOutput.SerilogSink(arguments.Verbose ? LogEventLevel.Debug : LogEventLevel.Information))
            .CreateLogger();

        using var exit = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            exit.Cancel();
        };

        try
        {
            return await RunAsync(arguments, exit.Token);
        }
        catch (OperationCanceledException) when (exit.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            ConsoleOutput.Error($"Fatal: {exception}");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static async Task<int> RunAsync(Arguments arguments, CancellationToken exit)
    {
        ConsoleOutput.Header("Wino Relay socket console");
        Trace("start");
        var apiUri = new Uri(arguments.Api == ApiTarget.Local ? LocalApiUrl : ProductionApiUrl);
        ConsoleOutput.KeyValue("Wino API", apiUri);

        // 1. The app's signed-in Wino Account.
        var dataFolder = arguments.DataFolder ?? AppSession.CandidateDataFolders(arguments.PackageFamilyName)
            .FirstOrDefault(folder => File.Exists(Path.Combine(folder, AppSession.DatabaseFileName)));
        if (dataFolder is null)
        {
            ConsoleOutput.Error("The app's database was not found. Looked in:");
            foreach (var folder in AppSession.CandidateDataFolders(arguments.PackageFamilyName)) ConsoleOutput.Muted("  " + folder);
            ConsoleOutput.Muted("Pass --data-folder <folder containing Wino210.db>.");
            return 2;
        }

        ConsoleOutput.KeyValue("App data folder", dataFolder);
        Trace("reading the app database");
        var session = await AppSession.OpenAsync(dataFolder, exit);
        Trace("app database read");
        if (session is null)
        {
            ConsoleOutput.Error("No Wino Account is signed in to the app. Sign in to Wino Mail first.");
            return 2;
        }

        PrintAccount(session);

        // 2. The console's own relay device and key.
        var consoleFolder = arguments.ConsoleFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinoRelayConsole");
        Directory.CreateDirectory(consoleFolder);
        ConsoleOutput.KeyValue("Console folder", consoleFolder);
        Trace("stores");
        var identityStore = new ConsoleIdentityStore(consoleFolder);
        var keyStore = new ConsoleKeyStore(consoleFolder);
        var platform = arguments.Platform ?? (OperatingSystem.IsMacOS() ? PushRelayPlatforms.MacOS : PushRelayPlatforms.Windows);

        using var apiHandler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                (arguments.Api == ApiTarget.Local && request.RequestUri?.IsLoopback == true) || errors == System.Net.Security.SslPolicyErrors.None,
        };
        using var apiHttp = new HttpClient(apiHandler) { BaseAddress = apiUri, Timeout = TimeSpan.FromSeconds(30) };
        Trace("http clients");
        var tokens = new ConsoleRelayTokenProvider(apiHttp, () => session.AccessToken);
        using var relay = new PushRelayClient(tokens);

        Trace("socket key");
        var deviceKey = await keyStore.GetOrCreateAsync(exit);
        Trace("socket key loaded");
        ConsoleOutput.KeyValue("Socket key", deviceKey.PublicKeyFingerprint[..16] + "…");

        await WaitForValidTokenAsync(session, tokens, exit);
        if (!await EnsureDeviceAsync(relay, identityStore, session, platform, deviceKey, exit))
            ConsoleOutput.Warning("no relay device yet; fix the cause, then 'c' registers and connects");

        Trace("device ready");
        // 3. The socket.
        await using var socket = new WinoRelaySocketClient(tokens, relay, identityStore, new SignalRRelayHubConnectionFactory(), keyStore);
        var pendingAcks = new List<string>();
        socket.Connected += (_, hello) => ConsoleOutput.Header(hello is null
            ? $"[{ConsoleOutput.Stamp()}] connected (no hello yet)"
            : $"[{ConsoleOutput.Stamp()}] connected: serverTime={hello.ServerTime:O} unackedEvents={hello.UnackedEvents} retentionHours={hello.RetentionHours}");
        socket.Disconnected += (_, d) => ConsoleOutput.Warning($"[{ConsoleOutput.Stamp()}] disconnected: {d.Reason}{(d.ByeReason is null ? "" : $" (bye {d.ByeReason})")}" +
            $"{(d.Problem is null ? "" : $" {d.Problem.Code}/{d.Problem.Reason}")}{(d.Error is null ? "" : $" {d.Error.GetType().Name}: {d.Error.Message}")}" +
            (d.WillReconnect ? " – reconnecting" : " – not reconnecting"));
        socket.EventFailed += (_, f) => ConsoleOutput.Error($"[{ConsoleOutput.Stamp()}] event failed: {f.Reason}\n{Pretty(f.Message)}");
        socket.EventReceived += (_, e) =>
        {
            ConsoleOutput.Event($"[{ConsoleOutput.Stamp()}] event {e.Kind ?? "?"} eid={e.EventId ?? "?"} cid={e.ClientAccountId ?? "-"}{(e.IsReplayed ? " (replayed)" : e.WasEncrypted ? " (decrypted)" : " (plain)")}");
            ConsoleOutput.Event(Pretty(e.Payload));
            if (e.EventId is null) return;
            if (arguments.NoAck)
            {
                lock (pendingAcks) pendingAcks.Add(e.EventId);
                return;
            }

            _ = AckAsync(socket, [e.EventId]);
        };

        await StartAsync(socket, exit);
        PrintCommands(arguments.NoAck);

        while (!exit.IsCancellationRequested)
        {
            var line = await Task.Run(() => ConsoleOutput.Ask("> "), exit);
            if (line is null) break;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var command = parts.Length == 0 ? string.Empty : parts[0].ToLowerInvariant();
            try
            {
                switch (command)
                {
                    case "":
                        break;
                    case "q" or "quit" or "exit":
                        await socket.StopAsync();
                        return 0;
                    case "s" or "status":
                        ConsoleOutput.KeyValue("State", socket.State);
                        ConsoleOutput.KeyValue("IsConnected", socket.IsConnected);
                        ConsoleOutput.KeyValue("Last hello", socket.LastHello is { } hello ? $"unacked={hello.UnackedEvents} serverTime={hello.ServerTime:O}" : "-");
                        lock (pendingAcks) ConsoleOutput.KeyValue("Pending acks", pendingAcks.Count);
                        PrintAccount(session);
                        break;
                    case "t" or "test":
                        var identity = await identityStore.LoadAsync(exit);
                        var test = await relay.SendPushTestAsync(identity!.DeviceId, exit);
                        ConsoleOutput.Info($"push-test: {test.Status} (the event should arrive over the socket)");
                        break;
                    case "a" or "ack":
                        string[] ids;
                        if (parts.Length > 1) ids = parts[1..];
                        else lock (pendingAcks) { ids = [.. pendingAcks]; pendingAcks.Clear(); }
                        if (ids.Length == 0) { ConsoleOutput.Muted("nothing to ack"); break; }
                        await AckAsync(socket, ids);
                        break;
                    case "d" or "device":
                        var current = await identityStore.LoadAsync(exit);
                        var device = await relay.GetDeviceAsync(current!.DeviceId, exit);
                        ConsoleOutput.Info(JsonSerializer.Serialize(device, PushRelayJsonContext.Default.PushRelayDevice));
                        break;
                    case "c" or "connect":
                        await WaitForValidTokenAsync(session, tokens, exit);
                        if (await identityStore.LoadAsync(exit) is null && !await EnsureDeviceAsync(relay, identityStore, session, platform, deviceKey, exit))
                            break;
                        await StartAsync(socket, exit);
                        break;
                    case "x" or "stop":
                        await socket.StopAsync();
                        break;
                    case "r" or "reload":
                        if (await session.ReloadAsync(exit)) { tokens.Invalidate(); PrintAccount(session); }
                        else ConsoleOutput.Error("The account could not be read again.");
                        break;
                    case "forget":
                        var forget = await identityStore.LoadAsync(exit);
                        if (forget is not null)
                        {
                            await socket.StopAsync();
                            try { await relay.DeleteDeviceAsync(forget.DeviceId, exit); } catch (PushRelayApiException ex) { ConsoleOutput.Warning($"delete failed: {ex.Code}"); }
                            await identityStore.DeleteAsync(exit);
                            ConsoleOutput.Info("console device forgotten; 'c' registers a new one");
                            await EnsureDeviceAsync(relay, identityStore, session, platform, deviceKey, exit);
                        }
                        break;
                    case "h" or "help" or "?":
                        PrintCommands(arguments.NoAck);
                        break;
                    default:
                        ConsoleOutput.Warning($"unknown command '{command}'");
                        break;
                }
            }
            catch (PushRelayApiException ex)
            {
                ConsoleOutput.Error($"relay: {ex.Message} {ex.Problem.Detail}");
            }
            catch (InvalidOperationException ex)
            {
                ConsoleOutput.Error(ex.Message);
            }
        }

        await socket.StopAsync();
        return 0;
    }

    /// <summary>Progress marks on stderr (unbuffered) for --verbose runs.</summary>
    private static void Trace(string step)
    {
        if (!Verbose) return;
        Console.Error.WriteLine($"[{ConsoleOutput.Stamp()}] … {step}");
        Console.Error.Flush();
    }

    private static bool Verbose;

    /// <summary>
    /// The console never refreshes the app's tokens, so an expired access token means: let the app refresh it (open
    /// Settings › Wino Account, which calls the API), then read the database again.
    /// </summary>
    private static async Task WaitForValidTokenAsync(AppSession session, ConsoleRelayTokenProvider tokens, CancellationToken exit)
    {
        while (!session.IsAccessTokenValid)
        {
            ConsoleOutput.Warning($"The app's access token expired at {session.Account.AccessTokenExpiresAtUtc:O}. Open Wino Mail › Settings › Wino Account so it refreshes the token.");
            var answer = await Task.Run(() => ConsoleOutput.Ask("Press Enter to read the app's tokens again (q quits): "), exit);
            if (answer is null || answer.Equals("q", StringComparison.OrdinalIgnoreCase))
                throw new OperationCanceledException();
            if (await session.ReloadAsync(exit))
            {
                tokens.Invalidate();
                PrintAccount(session);
            }
        }
    }

    private static async Task StartAsync(IWinoRelaySocketClient socket, CancellationToken exit)
    {
        ConsoleOutput.Info($"[{ConsoleOutput.Stamp()}] connecting…");
        var result = await socket.StartAsync(exit);
        var detail = result.Problem is null ? string.Empty : $" {result.Problem.Status} {result.Problem.Code}/{result.Problem.Reason} {result.Problem.Detail}";
        if (result.IsConnected) ConsoleOutput.Info($"[{ConsoleOutput.Stamp()}] start: {result.Outcome}");
        else ConsoleOutput.Error($"[{ConsoleOutput.Stamp()}] start: {result.Outcome}{detail}{(result.Error is null ? "" : $" ({result.Error.GetType().Name}: {result.Error.Message})")}");
    }

    private static async Task AckAsync(IWinoRelaySocketClient socket, string[] ids)
    {
        try
        {
            var count = await socket.AckAsync(ids);
            ConsoleOutput.Muted($"[{ConsoleOutput.Stamp()}] acked {ids.Length} → {count} newly acknowledged");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ConsoleOutput.Error($"ack failed: {ex.Message}");
        }
    }

    /// <summary>Registers the console's device for the signed-in account, or re-registers after a revocation.</summary>
    private static async Task<bool> EnsureDeviceAsync(IPushRelayClient relay, ConsoleIdentityStore identityStore, AppSession session, string platform,
        PushRelaySocketDeviceKey deviceKey, CancellationToken exit)
    {
        try
        {
            var identity = await identityStore.LoadAsync(exit);
            if (identity is not null && identity.WinoUserId == session.Account.Id && identity.Platform == platform)
            {
                try
                {
                    var device = await relay.GetDeviceAsync(identity.DeviceId, exit);
                    if (!device.SocketKeyRegistered)
                    {
                        await relay.UpdateDeviceAsync(identity.DeviceId, new PushRelayUpdateDeviceRequest(null, null, null, null, null, null, deviceKey.PublicKeySpkiBase64), exit);
                        ConsoleOutput.Info("socket key registered on the existing console device");
                    }

                    ConsoleOutput.KeyValue("Relay device", $"{identity.DeviceId} ({platform}, unacked={device.UnackedEvents})");
                    return true;
                }
                catch (PushRelayApiException ex) when (ex.IsDeviceRevoked || ex.Status == 404)
                {
                    ConsoleOutput.Warning("the console device was revoked; registering a new one");
                    await identityStore.DeleteAsync(exit);
                }
            }

            var registration = await relay.RegisterDeviceAsync(new PushRelayRegisterDeviceRequest(
                platform, null, "console", null, null, Environment.OSVersion.VersionString.Length > 64 ? Environment.OSVersion.VersionString[..64] : Environment.OSVersion.VersionString,
                $"Wino Relay Console ({Environment.MachineName})", deviceKey.PublicKeySpkiBase64), exit);
            await identityStore.SaveAsync(new PushRelayDeviceIdentity(registration.DeviceId, session.Account.Id, platform, DateTime.UtcNow), exit);
            ConsoleOutput.KeyValue("Relay device", $"{registration.DeviceId} ({platform}, new)");
            return true;
        }
        catch (PushRelayApiException ex)
        {
            ConsoleOutput.Error($"device registration failed: {ex.Message} {ex.Problem.Detail}");
            return false;
        }
    }

    private static void PrintAccount(AppSession session)
    {
        ConsoleOutput.KeyValue("Wino Account", $"{session.Account.Email} ({session.Account.Id})");
        ConsoleOutput.KeyValue("Access token", session.IsAccessTokenValid
            ? $"valid until {session.Account.AccessTokenExpiresAtUtc:O}"
            : $"EXPIRED at {session.Account.AccessTokenExpiresAtUtc:O} – open Wino Mail to refresh it, then press r");
    }

    private static void PrintCommands(bool noAck)
    {
        ConsoleOutput.Muted("  t  send a test push        a [eid…]  ack " + (noAck ? "pending events" : "given ids") + "      s  status");
        ConsoleOutput.Muted("  c  connect                 x  stop socket            d  device (GET /v1/devices/me)");
        ConsoleOutput.Muted("  r  re-read the app's tokens   forget  delete the console device   q  quit");
    }

    private static string Pretty(IReadOnlyDictionary<string, string> payload)
        => JsonSerializer.Serialize(new Dictionary<string, string>(payload), new JsonSerializerOptions(ConsoleJsonContext.Default.Options) { WriteIndented = true });
}

internal enum ApiTarget { Local, Production }

internal sealed record Arguments(ApiTarget Api, string? DataFolder, string? PackageFamilyName, string? ConsoleFolder, string? Platform, bool NoAck, bool Verbose, bool ShowHelp)
{
    public static bool TryParse(string[] args, out Arguments arguments, out string? error)
    {
        var api = ApiTarget.Production;
        string? data = null, package = null, consoleFolder = null, platform = null;
        bool noAck = false, verbose = false, help = false;
        error = null;
        for (var index = 0; index < args.Length && error is null; index++)
        {
            string? Value() => index + 1 < args.Length ? args[++index] : null;
            switch (args[index])
            {
                case "--api":
                    switch (Value()?.ToLowerInvariant())
                    {
                        case "local": api = ApiTarget.Local; break;
                        case "prod" or "production": api = ApiTarget.Production; break;
                        default: error = "--api takes local or prod."; break;
                    }
                    break;
                case "--data-folder": data = Value(); break;
                case "--package": package = Value(); break;
                case "--console-folder": consoleFolder = Value(); break;
                case "--platform":
                    platform = Value()?.ToLowerInvariant();
                    if (platform is not (PushRelayPlatforms.Windows or PushRelayPlatforms.MacOS)) error = "--platform takes windows or macos.";
                    break;
                case "--no-ack": noAck = true; break;
                case "--verbose" or "-v": verbose = true; break;
                case "--help" or "-h" or "/?": help = true; break;
                default: error = $"Unknown argument '{args[index]}'."; break;
            }
        }

        arguments = new Arguments(api, data, package, consoleFolder, platform, noAck, verbose, help);
        return error is null;
    }

    public static void PrintHelp() => Console.WriteLine("""
        Wino Relay socket console: opens the desktop relay socket as the Wino Account signed in to the app.

          --api local|prod          Wino API that issues relay tokens (default: prod)
          --data-folder <path>      the app's data folder (contains Wino210.db); found automatically otherwise
          --package <family name>   Windows: package whose LocalState to use (default: Wino Mail Preview)
          --console-folder <path>   where the console keeps its own relay device id and socket key
          --platform windows|macos  relay platform to register as (default: this host)
          --no-ack                  do not acknowledge events automatically ('a' acks the pending ones)
          --verbose                 show debug log lines from the services
        """);
}
