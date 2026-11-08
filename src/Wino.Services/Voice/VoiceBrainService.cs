#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Voice;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.Api.Contracts.Voice;
using Wino.Messaging.Client.Accounts;
using Wino.Messaging.UI;

namespace Wino.Services.Voice;

/// <summary>
/// Keeps this device's part of the Personal voice brain on the server. The mode is cached in the intelligence
/// database so the app knows it without the network; sections are pushed debounced, off the UI thread, and only when
/// their content hash differs from the last push.
/// </summary>
public sealed class VoiceBrainService : IVoiceBrainService,
    IRecipient<AccountCreatedMessage>,
    IRecipient<AccountRemovedMessage>,
    IRecipient<AccountFolderConfigurationUpdated>,
    IRecipient<AccountSynchronizationCompleted>,
    IRecipient<AccountCalendarSynchronizationStateChanged>,
    IRecipient<FolderRenamed>,
    IRecipient<FolderDeleted>,
    IRecipient<WinoAccountSignedInMessage>,
    IRecipient<WinoAccountSignedOutMessage>,
    IRecipient<AccountUpdatedMessage>,
    IRecipient<AccountMenuItemsReordered>
{
    private const string ModeKey = "mode";
    private const string BrainKey = "brain";
    private const string SectionKeyPrefix = "section:";
    private const string FactKeyPrefix = "fact:";
    private static readonly TimeSpan PushDebounce = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan DirtyRebuildInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan VoiceFreshness = TimeSpan.FromMinutes(5);

    private readonly IWinoAccountApiClient _api;
    private readonly IWinoAccountProfileService _profiles;
    private readonly IVoiceBrainStore _store;
    private readonly IAccountService _accounts;
    private readonly VoiceBrainSectionBuilder _sections;
    private readonly IMessenger _messenger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _pushLock = new(1, 1);
    private VoiceMode _mode = VoiceMode.Private;
    private long _lastPushRequest;
    private bool _isPushScheduled;
    private bool _isStarted;

    // Accounts whose local data changed since their section was last rebuilt; null in the set means every account.
    private readonly HashSet<Guid?> _dirty = [];
    private long _lastRebuild = Environment.TickCount64;
    private Timer? _dirtyTimer;

    public VoiceBrainService(
        IWinoAccountApiClient api,
        IWinoAccountProfileService profiles,
        IVoiceBrainStore store,
        IAccountService accounts,
        VoiceBrainSectionBuilder sections,
        IMessenger messenger)
    {
        _api = api;
        _profiles = profiles;
        _store = store;
        _accounts = accounts;
        _sections = sections;
        _messenger = messenger;
    }

    public VoiceMode CurrentMode => _mode;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_isStarted)
                return;

            _isStarted = true;
        }

        try
        {
            var cached = await _store.GetVoiceValueAsync(ModeKey, cancellationToken).ConfigureAwait(false);
            if (Enum.TryParse<VoiceMode>(cached, out var mode))
                _mode = mode;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read the cached voice mode.");
        }

        lock (_gate)
        {
            // Stop may have run while the cached mode was read.
            if (!_isStarted)
                return;

            _messenger.Register<AccountCreatedMessage>(this);
            _messenger.Register<AccountRemovedMessage>(this);
            _messenger.Register<AccountFolderConfigurationUpdated>(this);
            _messenger.Register<AccountSynchronizationCompleted>(this);
            _messenger.Register<AccountCalendarSynchronizationStateChanged>(this);
            _messenger.Register<FolderRenamed>(this);
            _messenger.Register<FolderDeleted>(this);
            _messenger.Register<WinoAccountSignedInMessage>(this);
            _messenger.Register<WinoAccountSignedOutMessage>(this);
            _messenger.Register<AccountUpdatedMessage>(this);
            _messenger.Register<AccountMenuItemsReordered>(this);

            // Starting sends nothing: the cached mode stands until a voice response or the settings page reports it,
            // and every section is only checked at the next rebuild, which sends it only when its value changed.
            _dirty.Add(null);
            _dirtyTimer = new Timer(_ => PushDirty(force: false), null, DirtyRebuildInterval, DirtyRebuildInterval);
        }
    }

    public void Stop()
    {
        Timer? timer;
        lock (_gate)
        {
            if (!_isStarted)
                return;

            _isStarted = false;
            timer = _dirtyTimer;
            _dirtyTimer = null;
            _dirty.Clear();
            _messenger.UnregisterAll(this);
        }

        timer?.Dispose();
    }

    public void NotifyVoiceStarting()
    {
        if (_mode == VoiceMode.Personal && TimeSpan.FromMilliseconds(Environment.TickCount64 - Volatile.Read(ref _lastRebuild)) >= VoiceFreshness)
            PushDirty(force: false);
    }

    private void MarkDirty(Guid? accountId)
    {
        lock (_gate)
            _dirty.Add(accountId);
    }

    private void PushDirty(bool force)
    {
        bool any;
        lock (_gate)
            any = _dirty.Count > 0;

        if ((any || force) && _mode == VoiceMode.Personal)
            RequestPush();
    }

    public async Task<VoiceMode> RefreshModeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await _profiles.HasActiveAccountAsync().ConfigureAwait(false))
                return _mode;

            var envelope = await _api.GetVoiceModeAsync(cancellationToken).ConfigureAwait(false);
            if (envelope.IsSuccess && envelope.Result is { } dto)
                await ApplyModeAsync(dto.Mode).ConfigureAwait(false);
            else
                Log.Information("Could not read the voice mode: {ErrorCode}.", envelope.ErrorCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Log.Warning(ex, "Could not read the voice mode.");
        }

        return _mode;
    }

    public async Task<bool> SetModeAsync(VoiceMode mode, CancellationToken cancellationToken = default)
    {
        try
        {
            var envelope = await _api.SetVoiceModeAsync(mode, cancellationToken).ConfigureAwait(false);
            if (!envelope.IsSuccess)
            {
                Log.Warning("Could not change the voice mode to {Mode}: {ErrorCode}.", mode, envelope.ErrorCode);
                return false;
            }

            await ApplyModeAsync(envelope.Result?.Mode ?? mode).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Log.Warning(ex, "Could not change the voice mode to {Mode}.", mode);
            return false;
        }
    }

    public void ApplyReportedMode(VoiceMode mode)
    {
        if (mode == _mode)
            return;

        _ = Task.Run(() => ApplyModeAsync(mode));
    }

    public void RequestPush()
    {
        if (_mode != VoiceMode.Personal)
            return;

        lock (_gate)
        {
            _lastPushRequest = Environment.TickCount64;
            if (_isPushScheduled)
                return;

            _isPushScheduled = true;
        }

        _ = Task.Run(PushWhenQuietAsync);
    }

    public async Task<VoiceBrainSnapshot?> GetBrainAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var envelope = await _api.GetVoiceBrainAsync(cancellationToken).ConfigureAwait(false);
            if (envelope.IsSuccess && envelope.Result is { } brain)
            {
                await ApplyModeAsync(brain.Mode).ConfigureAwait(false);
                await SaveAsync(BrainKey, JsonSerializer.Serialize(brain, WinoAccountApiJsonContext.Default.VoiceBrainDto)).ConfigureAwait(false);
                return new VoiceBrainSnapshot(brain, IsCached: false);
            }

            Log.Information("Could not download the voice brain: {ErrorCode}.", envelope.ErrorCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Log.Warning(ex, "Could not download the voice brain.");
        }

        try
        {
            var cached = await _store.GetVoiceValueAsync(BrainKey, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(cached))
                return null;

            var brain = JsonSerializer.Deserialize(cached, WinoAccountApiJsonContext.Default.VoiceBrainDto);
            return brain is null ? null : new VoiceBrainSnapshot(brain, IsCached: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Log.Warning(ex, "Could not read the cached voice brain.");
            return null;
        }
    }

    public async Task AddFactAsync(AddBrainFactRequest request, CancellationToken cancellationToken = default)
    {
        if (_mode != VoiceMode.Personal)
            return;

        try
        {
            // The same answer again (the same label for the same person, account, folder or calendar) is already known.
            var key = FactKey(request.Kind, request.Label, request.DefaultUse);
            var value = FactValue(request);
            if (string.Equals(await _store.GetVoiceValueAsync(key, cancellationToken).ConfigureAwait(false), value, StringComparison.Ordinal))
                return;

            var envelope = await _api.AddVoiceBrainFactAsync(request, cancellationToken).ConfigureAwait(false);
            if (envelope.IsSuccess)
            {
                await _store.SetVoiceValueAsync(key, value, cancellationToken).ConfigureAwait(false);
                return;
            }

            Log.Information("Could not save a learned voice fact: {ErrorCode}.", envelope.ErrorCode);
            if (envelope.ErrorCode == ApiErrorCodes.VoicePersonalModeOff)
                await ApplyModeAsync(VoiceMode.Private).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Log.Warning(ex, "Could not save a learned voice fact.");
        }
    }

    public async Task<bool> DeleteFactAsync(string factId, CancellationToken cancellationToken = default)
    {
        try
        {
            var envelope = await _api.DeleteVoiceBrainFactAsync(factId, cancellationToken).ConfigureAwait(false);
            if (envelope.IsSuccess || envelope.ErrorCode == ApiErrorCodes.VoiceBrainFactNotFound)
            {
                await ForgetFactAsync(factId, cancellationToken).ConfigureAwait(false);
                return true;
            }

            Log.Warning("Could not delete a learned voice fact: {ErrorCode}.", envelope.ErrorCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Log.Warning(ex, "Could not delete a learned voice fact.");
        }

        return false;
    }

    public async Task<bool> DeleteBrainAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var envelope = await _api.DeleteVoiceBrainAsync(cancellationToken).ConfigureAwait(false);
            if (!envelope.IsSuccess)
            {
                Log.Warning("Could not delete the voice brain: {ErrorCode}.", envelope.ErrorCode);
                return false;
            }

            // The mode stays Personal; forgetting the pushed hashes lets the next trigger push the sections again.
            await ClearCachesAsync(keepMode: true).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Log.Warning(ex, "Could not delete the voice brain.");
            return false;
        }
    }

    // Things the user did (an account added, a folder renamed, deleted or reconfigured) are pushed soon; routine
    // synchronization only marks the account dirty for the next rebuild.
    public void Receive(AccountCreatedMessage message)
    {
        MarkDirty(message.Account?.Id);
        RequestPush();
    }

    public void Receive(AccountFolderConfigurationUpdated message)
    {
        MarkDirty(null);
        RequestPush();
    }

    public void Receive(FolderRenamed message)
    {
        MarkDirty(null);
        RequestPush();
    }

    public void Receive(FolderDeleted message)
    {
        MarkDirty(null);
        RequestPush();
    }

    // A renamed account or a new account order changes how the user names accounts; an update that changed neither
    // hashes the same and sends nothing.
    public void Receive(AccountUpdatedMessage message)
    {
        MarkDirty(message.Account?.Id);
        RequestPush();
    }

    public void Receive(AccountMenuItemsReordered message)
    {
        MarkDirty(null);
        RequestPush();
    }

    public void Receive(AccountSynchronizationCompleted message)
    {
        if (message.Type is MailSynchronizationType.FoldersOnly or MailSynchronizationType.FullFolders
            or MailSynchronizationType.Alias or MailSynchronizationType.Categories or MailSynchronizationType.UpdateProfile)
            MarkDirty(message.AccountId);
    }

    public void Receive(AccountCalendarSynchronizationStateChanged message)
    {
        if (!message.IsSynchronizationInProgress)
            MarkDirty(message.AccountId);
    }

    public void Receive(AccountRemovedMessage message) => _ = Task.Run(() => DeleteSectionAsync(message.Account));

    public void Receive(WinoAccountSignedInMessage message) => _ = Task.Run(() => RefreshThenPushAsync(CancellationToken.None));

    public void Receive(WinoAccountSignedOutMessage message) => _ = Task.Run(() => ApplyModeAsync(VoiceMode.Private));

    private async Task RefreshThenPushAsync(CancellationToken cancellationToken)
    {
        await RefreshModeAsync(cancellationToken).ConfigureAwait(false);
        MarkDirty(null);
        RequestPush();
    }

    private async Task ApplyModeAsync(VoiceMode mode)
    {
        var changed = mode != _mode;
        _mode = mode;

        if (mode == VoiceMode.Private)
            await ClearCachesAsync(keepMode: false).ConfigureAwait(false);
        else
            await SaveAsync(ModeKey, mode.ToString()).ConfigureAwait(false);

        if (!changed)
            return;

        _messenger.Send(new VoiceModeChanged(mode));
        if (mode == VoiceMode.Personal)
        {
            MarkDirty(null);
            RequestPush();
        }
    }

    private async Task ClearCachesAsync(bool keepMode)
    {
        try
        {
            await _store.ClearVoiceValuesAsync().ConfigureAwait(false);
            if (keepMode || _mode == VoiceMode.Personal)
                await _store.SetVoiceValueAsync(ModeKey, _mode.ToString()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not clear the voice caches.");
        }
    }

    private async Task SaveAsync(string key, string? value)
    {
        try
        {
            await _store.SetVoiceValueAsync(key, value).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not save the voice value {Key}.", key);
        }
    }

    /// <summary>Waits until no push was requested for <see cref="PushDebounce"/>, then pushes every section once.</summary>
    private async Task PushWhenQuietAsync()
    {
        try
        {
            while (true)
            {
                long last;
                lock (_gate)
                    last = _lastPushRequest;

                var wait = PushDebounce - TimeSpan.FromMilliseconds(Environment.TickCount64 - last);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait).ConfigureAwait(false);
                    continue;
                }

                lock (_gate)
                {
                    if (_lastPushRequest != last)
                        continue;

                    _isPushScheduled = false;
                }

                await PushAllAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Pushing the voice brain failed.");
            lock (_gate)
                _isPushScheduled = false;
        }
    }

    private async Task PushAllAsync(CancellationToken cancellationToken)
    {
        await _pushLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_mode != VoiceMode.Personal || !await _profiles.HasActiveAccountAsync().ConfigureAwait(false))
                return;

            HashSet<Guid?> dirty;
            lock (_gate)
            {
                dirty = [.. _dirty];
                _dirty.Clear();
            }

            Volatile.Write(ref _lastRebuild, Environment.TickCount64);
            var accounts = await _accounts.GetAccountsAsync().ConfigureAwait(false) ?? [];
            foreach (var account in accounts.OrderBy(static account => account.Order))
            {
                if (_mode != VoiceMode.Personal)
                    return;

                if (!dirty.Contains(null) && !dirty.Contains(account.Id))
                    continue;

                if (!await PushSectionAsync(account, cancellationToken).ConfigureAwait(false))
                {
                    MarkDirty(account.Id);
                    return;
                }
            }
        }
        finally
        {
            _pushLock.Release();
        }
    }

    /// <returns>False when pushing should stop: the server is in Private mode or can't be reached.</returns>
    private async Task<bool> PushSectionAsync(MailAccount account, CancellationToken cancellationToken)
    {
        BrainSection? section;
        try
        {
            section = await _sections.BuildAsync(account, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "Could not derive the voice brain section of account {AccountId}.", account.Id);
            return true;
        }

        if (section is null)
            return true;

        var key = SectionKey(section.Mailbox);
        var hash = VoiceBrainSectionBuilder.ValueHash(section);
        if (string.Equals(await _store.GetVoiceValueAsync(key, cancellationToken).ConfigureAwait(false), hash, StringComparison.Ordinal))
            return true;

        var envelope = await _api.PushVoiceBrainSectionAsync(new PushBrainSectionRequest(section, null), cancellationToken).ConfigureAwait(false);
        if (envelope.IsSuccess)
        {
            await _store.SetVoiceValueAsync(key, hash, cancellationToken).ConfigureAwait(false);
            return true;
        }

        Log.Information("Could not push the voice brain section of account {AccountId}: {ErrorCode}.", account.Id, envelope.ErrorCode);
        if (envelope.ErrorCode == ApiErrorCodes.VoicePersonalModeOff)
        {
            await ApplyModeAsync(VoiceMode.Private).ConfigureAwait(false);
            return false;
        }

        return true;
    }

    private async Task DeleteSectionAsync(MailAccount account)
    {
        if (VoiceProviderKinds.ToMailboxKey(account) is not { } mailbox)
            return;

        try
        {
            await _store.SetVoiceValueAsync(SectionKey(mailbox), null).ConfigureAwait(false);
            if (!await _profiles.HasActiveAccountAsync().ConfigureAwait(false))
                return;

            // Sent in either mode: the cached mode may be stale when another device turned Personal on.
            var envelope = await _api.DeleteVoiceBrainSectionAsync(mailbox).ConfigureAwait(false);
            if (!envelope.IsSuccess)
                Log.Information("Could not delete the voice brain section of a removed account: {ErrorCode}.", envelope.ErrorCode);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not delete the voice brain section of a removed account.");
        }
    }

    private static string SectionKey(BrainMailboxKey mailbox) => $"{SectionKeyPrefix}{mailbox.Provider}:{mailbox.Address}";

    // A default account is one per use, so the use is part of its key; every other fact is one per label.
    private static string FactKey(BrainFactKind kind, string label, BrainDefaultUse? use)
        => kind == BrainFactKind.DefaultAccount ? $"{FactKeyPrefix}{kind}:{use}" : $"{FactKeyPrefix}{kind}:{label.Trim().ToLowerInvariant()}";

    private static string FactValue(AddBrainFactRequest request)
        => string.Join('|', request.PersonAddressHash, request.Mailbox?.Provider, request.Mailbox is { } mailbox ? BrainMailboxKey.Normalize(mailbox.Address) : null, request.TargetRemoteId,
            request.SenderKind, string.Join(',', (request.Members ?? []).Select(static member => member.AddressHash).Order(StringComparer.Ordinal)));

    /// <summary>Forgets the sent marker of a deleted fact, found by its id in the cached brain, so the answer can be learned again.</summary>
    private async Task ForgetFactAsync(string factId, CancellationToken cancellationToken)
    {
        try
        {
            var cached = await _store.GetVoiceValueAsync(BrainKey, cancellationToken).ConfigureAwait(false);
            var brain = string.IsNullOrWhiteSpace(cached) ? null : JsonSerializer.Deserialize(cached, WinoAccountApiJsonContext.Default.VoiceBrainDto);
            if (brain?.Facts?.FirstOrDefault(fact => fact.Id == factId) is { } fact)
                await _store.SetVoiceValueAsync(FactKey(fact.Kind, fact.Label, fact.DefaultUse), null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Log.Warning(ex, "Could not forget a deleted voice fact.");
        }
    }
}
