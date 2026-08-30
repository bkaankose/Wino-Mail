using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using MailKit;

namespace Wino.Core.Diagnostics;

public enum MailProtocol
{
    Imap,
    Smtp,
    Pop3
}

/// <summary>
/// MailKit protocol logger that preserves protocol commands and responses while removing
/// authentication secrets and message literal/body payloads.
/// </summary>
public sealed class WinoProtocolLogger : IProtocolLogger
{
    public const string ProtocolLogFolderName = "ProtocolLogs";
    public const string ImapProtocolLogFileName = "imap.log";
    public const string SmtpProtocolLogFileName = "smtp.log";
    public const string Pop3ProtocolLogFileName = "pop3.log";

    private static readonly ConcurrentDictionary<string, object> FileLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex LiteralMarkerRegex = new(@"\{(?<length>\d+)\+?\}\r?\n$", RegexOptions.Compiled);
    private static readonly Regex BdatRegex = new(@"^BDAT\s+(?<length>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ProtocolLogger _inner;
    private readonly MailProtocol _protocol;
    private readonly object _writeLock;
    private readonly DirectionState _clientState = new();
    private readonly DirectionState _serverState = new();
    private bool _pop3MessageResponseExpected;
    private bool _disposed;

    public IAuthenticationSecretDetector AuthenticationSecretDetector
    {
        get => _inner.AuthenticationSecretDetector;
        set => _inner.AuthenticationSecretDetector = value;
    }

    public static string GetAccountLogFolder(string applicationDataFolderPath, Guid accountId)
        => Path.Combine(applicationDataFolderPath, ProtocolLogFolderName, accountId.ToString("N"));

    public static string GetAccountLogFilePath(
        string applicationDataFolderPath,
        Guid accountId,
        MailProtocol protocol)
        => Path.Combine(GetAccountLogFolder(applicationDataFolderPath, accountId), GetProtocolLogFileName(protocol));

    public static WinoProtocolLogger CreateAccountLogger(
        string applicationDataFolderPath,
        Guid accountId,
        MailProtocol protocol)
    {
        var accountFolder = GetAccountLogFolder(applicationDataFolderPath, accountId);
        Directory.CreateDirectory(accountFolder);

        var logPath = GetAccountLogFilePath(applicationDataFolderPath, accountId, protocol);
        var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        return new WinoProtocolLogger(
            stream,
            protocol,
            leaveOpen: false,
            FileLocks.GetOrAdd(logPath, static _ => new object()));
    }

    private static string GetProtocolLogFileName(MailProtocol protocol)
        => protocol switch
        {
            MailProtocol.Imap => ImapProtocolLogFileName,
            MailProtocol.Smtp => SmtpProtocolLogFileName,
            MailProtocol.Pop3 => Pop3ProtocolLogFileName,
            _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null)
        };

    public WinoProtocolLogger(Stream stream, MailProtocol protocol, bool leaveOpen = true)
        : this(stream, protocol, leaveOpen, new object())
    {
    }

    private WinoProtocolLogger(Stream stream, MailProtocol protocol, bool leaveOpen, object writeLock)
    {
        ArgumentNullException.ThrowIfNull(stream);

        _protocol = protocol;
        _writeLock = writeLock;
        _inner = new ProtocolLogger(stream, leaveOpen)
        {
            ClientPrefix = $"{protocol.ToString().ToUpperInvariant()} C: ",
            ServerPrefix = $"{protocol.ToString().ToUpperInvariant()} S: ",
            LogTimestamps = true,
            RedactSecrets = true
        };
    }

    public void LogConnect(Uri uri)
    {
        lock (_writeLock)
        {
            ThrowIfDisposed();
            _inner.LogConnect(uri);
        }
    }

    public void LogClient(byte[] buffer, int offset, int count)
        => Log(buffer, offset, count, _clientState, isClient: true);

    public void LogServer(byte[] buffer, int offset, int count)
        => Log(buffer, offset, count, _serverState, isClient: false);

    private void Log(byte[] buffer, int offset, int count, DirectionState state, bool isClient)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (offset > buffer.Length || count > buffer.Length - offset)
            throw new ArgumentOutOfRangeException(nameof(count));

        lock (_writeLock)
        {
            ThrowIfDisposed();
            Process(buffer, offset, count, state, isClient);
        }
    }

    private void Process(byte[] buffer, int offset, int count, DirectionState state, bool isClient)
    {
        var end = offset + count;

        while (offset < end)
        {
            if (state.RedactedBytesRemaining > 0)
            {
                WriteRedactionNoticeIfNeeded(state, isClient);

                var skipped = (int)Math.Min(state.RedactedBytesRemaining, end - offset);
                state.RedactedBytesRemaining -= skipped;
                offset += skipped;
                continue;
            }

            var newlineIndex = Array.IndexOf(buffer, (byte)'\n', offset, end - offset);
            if (newlineIndex < 0)
            {
                state.LineBuffer.Write(buffer, offset, end - offset);
                break;
            }

            state.LineBuffer.Write(buffer, offset, newlineIndex - offset + 1);
            offset = newlineIndex + 1;

            var line = state.LineBuffer.ToArray();
            state.LineBuffer.SetLength(0);

            if (_protocol == MailProtocol.Smtp && isClient && state.IsSmtpData)
            {
                WriteRedactionNoticeIfNeeded(state, isClient);

                if (IsSmtpDataTerminator(line))
                {
                    state.IsSmtpData = false;
                    Write(line, isClient);
                }

                continue;
            }

            if (_protocol == MailProtocol.Pop3 && !isClient && state.IsPop3MessageData)
            {
                WriteRedactionNoticeIfNeeded(state, isClient);

                if (IsMultilineTerminator(line))
                {
                    state.IsPop3MessageData = false;
                    Write(line, isClient);
                }

                continue;
            }

            Write(line, isClient);
            UpdateRedactionState(line, state, isClient);
        }
    }

    private void UpdateRedactionState(byte[] line, DirectionState state, bool isClient)
    {
        var text = Encoding.ASCII.GetString(line);

        if (_protocol == MailProtocol.Imap)
        {
            var literalMatch = LiteralMarkerRegex.Match(text);
            if (literalMatch.Success && long.TryParse(literalMatch.Groups["length"].Value, out var literalLength))
            {
                state.RedactedBytesRemaining = literalLength;
                state.RedactionNoticePending = literalLength > 0;
            }

            return;
        }

        if (_protocol == MailProtocol.Pop3)
        {
            if (isClient)
            {
                var command = text.TrimStart();
                _pop3MessageResponseExpected = command.StartsWith("RETR ", StringComparison.OrdinalIgnoreCase)
                    || command.StartsWith("TOP ", StringComparison.OrdinalIgnoreCase);
            }
            else if (_pop3MessageResponseExpected)
            {
                _pop3MessageResponseExpected = false;

                if (text.StartsWith("+OK", StringComparison.OrdinalIgnoreCase))
                {
                    state.IsPop3MessageData = true;
                    state.RedactionNoticePending = true;
                }
            }

            return;
        }

        if (!isClient)
            return;

        var trimmed = text.Trim();
        if (trimmed.Equals("DATA", StringComparison.OrdinalIgnoreCase))
        {
            state.IsSmtpData = true;
            state.RedactionNoticePending = true;
            return;
        }

        var bdatMatch = BdatRegex.Match(trimmed);
        if (bdatMatch.Success && long.TryParse(bdatMatch.Groups["length"].Value, out var bdatLength))
        {
            state.RedactedBytesRemaining = bdatLength;
            state.RedactionNoticePending = bdatLength > 0;
        }
    }

    private static bool IsSmtpDataTerminator(byte[] line)
        => IsMultilineTerminator(line);

    private static bool IsMultilineTerminator(byte[] line)
        => Encoding.ASCII.GetString(line).TrimEnd('\r', '\n') == ".";

    private void WriteRedactionNoticeIfNeeded(DirectionState state, bool isClient)
    {
        if (!state.RedactionNoticePending)
            return;

        state.RedactionNoticePending = false;
        Write(Encoding.UTF8.GetBytes("[message content redacted]\r\n"), isClient);
    }

    private void Write(byte[] data, bool isClient)
    {
        if (isClient)
            _inner.LogClient(data, 0, data.Length);
        else
            _inner.LogServer(data, 0, data.Length);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            if (_disposed)
                return;

            FlushPending(_clientState, isClient: true);
            FlushPending(_serverState, isClient: false);
            _inner.Dispose();
            _disposed = true;
        }
    }

    private void FlushPending(DirectionState state, bool isClient)
    {
        if (state.LineBuffer.Length == 0)
            return;

        var pending = state.LineBuffer.ToArray();
        state.LineBuffer.SetLength(0);

        if (state.IsSmtpData || state.IsPop3MessageData)
        {
            WriteRedactionNoticeIfNeeded(state, isClient);
            return;
        }

        Write(pending, isClient);
    }

    private sealed class DirectionState
    {
        public MemoryStream LineBuffer { get; } = new();
        public long RedactedBytesRemaining { get; set; }
        public bool RedactionNoticePending { get; set; }
        public bool IsSmtpData { get; set; }
        public bool IsPop3MessageData { get; set; }
    }
}
