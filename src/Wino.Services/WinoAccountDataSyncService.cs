#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Api.Contracts.Users;
using Wino.Messaging.Client.Accounts;
using Wino.Messaging.UI;

namespace Wino.Services;

/// <summary>
/// Builds the sync snapshot from everything the app owns, encrypts it on this device and moves it
/// through the Wino Account or a file. The service never sees the plaintext. Passwords, tokens
/// and mail content are never part of the snapshot.
/// </summary>
public sealed class WinoAccountDataSyncService : IWinoAccountDataSyncService
{
    private const int DefaultMaxConcurrentClients = 5;
    private const string FileNamePrefix = "wino-backup-";

    private readonly IWinoAccountProfileService _profileService;
    private readonly IPreferencesService _preferencesService;
    private readonly IAccountService _accountService;
    private readonly IFolderService _folderService;
    private readonly ISignatureService _signatureService;
    private readonly ISyncSnapshotKeyService? _keyService;
    private readonly IEmailTemplateService? _templateService;
    private readonly IMailFilterService? _filterService;
    private readonly IKeyboardShortcutService? _shortcutService;
    private readonly IMailCategoryService? _categoryService;
    private readonly INewThemeService? _themeService;
    private readonly IStatePersistanceService? _stateService;
    private readonly ILogger _logger = Log.ForContext<WinoAccountDataSyncService>();

    public WinoAccountDataSyncService(
        IWinoAccountProfileService profileService,
        IPreferencesService preferencesService,
        IAccountService accountService,
        IFolderService folderService,
        ISignatureService signatureService,
        ISyncSnapshotKeyService? keyService = null,
        IEmailTemplateService? templateService = null,
        IMailFilterService? filterService = null,
        IKeyboardShortcutService? shortcutService = null,
        IMailCategoryService? categoryService = null,
        INewThemeService? themeService = null,
        IStatePersistanceService? stateService = null)
    {
        _profileService = profileService;
        _preferencesService = preferencesService;
        _accountService = accountService;
        _folderService = folderService;
        _signatureService = signatureService;
        _keyService = keyService;
        _templateService = templateService;
        _filterService = filterService;
        _shortcutService = shortcutService;
        _categoryService = categoryService;
        _themeService = themeService;
        _stateService = stateService;
    }

    public async Task<WinoAccountSyncExportResult> ExportAsync(WinoAccountSyncSelection selection, SyncSnapshotSecretPrompt? secretPrompt = null, CancellationToken cancellationToken = default)
    {
        _ = await _profileService.GetActiveAccountAsync().ConfigureAwait(false)
            ?? throw WinoAccountApiException.SignInRequired();

        var key = await DeriveNewKeyAsync(secretPrompt, cancellationToken).ConfigureAwait(false);
        var prepared = await PrepareExportAsync(selection).ConfigureAwait(false);
        var payload = Seal(prepared.Document, key);

        await _profileService.PutSyncSnapshotAsync(payload, null, cancellationToken).ConfigureAwait(false);

        // The structured mailbox list stays on the server in the clear: it holds no secrets and
        // mail intelligence proves mailbox ownership against it.
        if (selection.IncludeAccounts && prepared.Document.Mailboxes != null)
        {
            await _profileService.ReplaceMailboxesAsync(new ReplaceUserMailboxesRequestDto { Mailboxes = prepared.Document.Mailboxes }, cancellationToken).ConfigureAwait(false);
        }

        return prepared.ExportResult;
    }

    public async Task<WinoAccountSyncFileExportResult> ExportToFileAsync(WinoAccountSyncSelection selection, SyncSnapshotSecretPrompt? secretPrompt = null, CancellationToken cancellationToken = default)
    {
        var key = await DeriveNewKeyAsync(secretPrompt, cancellationToken).ConfigureAwait(false);
        var prepared = await PrepareExportAsync(selection).ConfigureAwait(false);

        return new WinoAccountSyncFileExportResult
        {
            Content = Seal(prepared.Document, key),
            FileName = $"{FileNamePrefix}{DateTime.Now:yyyyMMdd-HHmm}{SyncSnapshotFormat.FileExtension}",
            ExportResult = prepared.ExportResult
        };
    }

    public async Task<WinoAccountSyncImportResult> ImportAsync(WinoAccountSyncSelection selection, SyncSnapshotSecretPrompt? secretPrompt = null, CancellationToken cancellationToken = default)
    {
        var download = await _profileService.GetSyncSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (download == null)
        {
            return new WinoAccountSyncImportResult
            {
                IncludedPreferences = selection.IncludePreferences,
                IncludedAccounts = selection.IncludeAccounts
            };
        }

        var document = await OpenAsync(download.Payload, secretPrompt, cancellationToken).ConfigureAwait(false);

        return await ApplyDocumentAsync(selection, document, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WinoAccountSyncImportResult> ImportFromFileAsync(byte[] content, SyncSnapshotSecretPrompt? secretPrompt = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!SyncSnapshotCryptography.IsSnapshot(content))
        {
            // Plain JSON exports from older builds are no longer accepted.
            throw new SyncSnapshotInvalidFileException("The file is not a Wino backup.");
        }

        var document = await OpenAsync(content, secretPrompt, cancellationToken).ConfigureAwait(false);
        var selection = new WinoAccountSyncSelection(
            IncludePreferences: !string.IsNullOrWhiteSpace(document.PreferencesJson),
            IncludeAccounts: document.Mailboxes?.Count > 0);

        return await ApplyDocumentAsync(selection, document, cancellationToken).ConfigureAwait(false);
    }

    public void ApplyAppearance(SyncSnapshotAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);

        if (_themeService != null)
        {
            if (Enum.IsDefined((WindowBackdropType)appearance.BackdropType))
            {
                _themeService.CurrentBackdropType = (WindowBackdropType)appearance.BackdropType;
            }

            if (Enum.IsDefined((ApplicationElementTheme)appearance.RootTheme))
            {
                _themeService.RootTheme = (ApplicationElementTheme)appearance.RootTheme;
            }

            if (appearance.AccentColor != null && !string.Equals(_themeService.AccentColor, appearance.AccentColor, StringComparison.OrdinalIgnoreCase))
            {
                _themeService.AccentColor = appearance.AccentColor;
            }

            // Built-in theme ids are the same on every device. A custom theme only exists where it
            // was made, so an unknown id is left alone rather than selecting nothing.
            if (appearance.ThemeId is { } themeId && themeId != _themeService.CurrentApplicationThemeId)
            {
                _themeService.CurrentApplicationThemeId = themeId;
            }
        }

        if (_stateService != null)
        {
            if (appearance.OpenPaneLength > 0) _stateService.OpenPaneLength = appearance.OpenPaneLength;
            if (appearance.MailListPaneLength > 0) _stateService.MailListPaneLength = appearance.MailListPaneLength;
            if (Enum.IsDefined((CalendarDisplayType)appearance.CalendarDisplayType)) _stateService.CalendarDisplayType = (CalendarDisplayType)appearance.CalendarDisplayType;
            if (appearance.DayDisplayCount > 0) _stateService.DayDisplayCount = appearance.DayDisplayCount;
        }
    }

    #region Keys

    /// <summary>
    /// Asks for a new backup password and derives a key with a fresh salt. Nothing is cached.
    /// </summary>
    private async Task<SyncSnapshotKey> DeriveNewKeyAsync(SyncSnapshotSecretPrompt? secretPrompt, CancellationToken cancellationToken)
    {
        var keyService = RequireKeyService();
        keyService.DeleteLegacyKeyCache();

        var secret = await AskSecretAsync(secretPrompt, new SyncSnapshotSecretRequest(IsPassphrase: true, WasRejected: false, IsNewBackup: true)).ConfigureAwait(false);

        return await keyService.DeriveAsync(secret, keyService.CreateParameters(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks for the password the snapshot was made with until it opens or the user cancels.
    /// Snapshots from earlier builds are locked with the Wino Account password of that time.
    /// </summary>
    private async Task<WinoSyncSnapshotDocument> OpenAsync(byte[] payload, SyncSnapshotSecretPrompt? secretPrompt, CancellationToken cancellationToken)
    {
        var keyService = RequireKeyService();
        keyService.DeleteLegacyKeyCache();

        var header = SyncSnapshotCryptography.ReadHeader(payload);
        var parameters = header.ToKeyParameters();
        var isPassphrase = header.KeySource == SyncSnapshotFormat.KeySourcePassphrase;

        for (var wasRejected = false; ; wasRejected = true)
        {
            var secret = await AskSecretAsync(secretPrompt, new SyncSnapshotSecretRequest(isPassphrase, wasRejected)).ConfigureAwait(false);
            var key = await keyService.DeriveAsync(secret, parameters, cancellationToken).ConfigureAwait(false);

            try
            {
                return Unseal(payload, key.Key);
            }
            catch (SyncSnapshotDecryptionException)
            {
                // Wrong password. Ask again; cancelling the prompt ends the loop.
            }
        }
    }

    private static async Task<string> AskSecretAsync(SyncSnapshotSecretPrompt? secretPrompt, SyncSnapshotSecretRequest request)
    {
        if (secretPrompt == null)
        {
            throw new SyncSnapshotKeyRequiredException("A backup password is needed and no prompt was supplied.");
        }

        var secret = await secretPrompt(request).ConfigureAwait(false);
        if (string.IsNullOrEmpty(secret))
        {
            throw new SyncSnapshotKeyRequiredException("The snapshot was not unlocked.");
        }

        return secret;
    }

    private ISyncSnapshotKeyService RequireKeyService()
        => _keyService ?? throw new InvalidOperationException("Sync snapshots need a key service.");

    private static byte[] Seal(WinoSyncSnapshotDocument document, SyncSnapshotKey key)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(document, WinoSyncSnapshotJsonContext.Default.WinoSyncSnapshotDocument);

        return SyncSnapshotCryptography.Encrypt(plaintext, key);
    }

    private static WinoSyncSnapshotDocument Unseal(byte[] payload, byte[] key)
    {
        var plaintext = SyncSnapshotCryptography.Decrypt(payload, key);

        try
        {
            var document = JsonSerializer.Deserialize(plaintext, WinoSyncSnapshotJsonContext.Default.WinoSyncSnapshotDocument);

            return document ?? throw new SyncSnapshotInvalidFileException("The snapshot is empty.");
        }
        catch (JsonException ex)
        {
            throw new SyncSnapshotInvalidFileException("The snapshot content could not be read.", ex);
        }
    }

    #endregion

    #region Export

    private async Task<PreparedSyncExport> PrepareExportAsync(WinoAccountSyncSelection selection)
    {
        var document = new WinoSyncSnapshotDocument { ExportedAtUtc = DateTime.UtcNow };
        var exportedAccountDataCount = 0;
        var exportedAppDataCount = 0;

        var accounts = (await _accountService.GetAccountsAsync().ConfigureAwait(false)).OrderBy(a => a.Order).ToList();

        if (selection.IncludePreferences)
        {
            document.PreferencesJson = _preferencesService.ExportPreferences();
            document.Templates = await ExportTemplatesAsync().ConfigureAwait(false);
            document.Shortcuts = await ExportShortcutsAsync().ConfigureAwait(false);
            document.Appearance = ExportAppearance();

            exportedAppDataCount += (document.Templates?.Count ?? 0) + (document.Shortcuts?.Count ?? 0);
        }

        if (selection.IncludeAccounts)
        {
            document.Mailboxes = [];

            foreach (var account in accounts)
            {
                var mailbox = await MapMailboxAsync(account).ConfigureAwait(false);
                document.Mailboxes.Add(mailbox);

                if (mailbox.Signatures?.Count > 0 || mailbox.Folders?.Count > 0)
                {
                    exportedAccountDataCount++;
                }
            }

            document.Filters = await ExportFiltersAsync(accounts).ConfigureAwait(false);
            document.Categories = await ExportCategoriesAsync(accounts).ConfigureAwait(false);
            document.Aliases = await ExportAliasesAsync(accounts).ConfigureAwait(false);
            document.MergedInboxes = ExportMergedInboxes(accounts);
            document.AccountCapabilities = ExportAccountCapabilities(accounts);

            exportedAppDataCount += (document.Filters?.Count ?? 0) + (document.Categories?.Count ?? 0)
                + (document.Aliases?.Count ?? 0) + (document.MergedInboxes?.Count ?? 0);
        }

        return new PreparedSyncExport(
            document,
            new WinoAccountSyncExportResult
            {
                IncludedPreferences = selection.IncludePreferences,
                IncludedAccounts = selection.IncludeAccounts,
                ExportedMailboxCount = document.Mailboxes?.Count ?? 0,
                ExportedAccountDataCount = exportedAccountDataCount,
                ExportedAppDataCount = exportedAppDataCount
            });
    }

    private async Task<List<SnapshotTemplate>?> ExportTemplatesAsync()
    {
        if (_templateService == null) return null;

        var templates = await _templateService.GetEmailTemplatesAsync().ConfigureAwait(false);

        return templates
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .Select(a => new SnapshotTemplate { Name = a.Name.Trim(), Description = a.Description ?? string.Empty, HtmlContent = a.HtmlContent ?? string.Empty })
            .ToList();
    }

    private async Task<List<SnapshotShortcut>?> ExportShortcutsAsync()
    {
        if (_shortcutService == null) return null;

        var shortcuts = await _shortcutService.GetKeyboardShortcutsAsync().ConfigureAwait(false);

        return shortcuts
            .Where(a => !string.IsNullOrWhiteSpace(a.Key))
            .Select(a => new SnapshotShortcut
            {
                Mode = (int)a.Mode,
                Key = a.Key,
                ModifierKeys = (int)a.ModifierKeys,
                Action = (int)a.Action,
                IsEnabled = a.IsEnabled
            })
            .ToList();
    }

    private SyncSnapshotAppearance? ExportAppearance()
    {
        if (_themeService == null && _stateService == null) return null;

        return new SyncSnapshotAppearance
        {
            RootTheme = (int)(_themeService?.RootTheme ?? ApplicationElementTheme.Default),
            ThemeId = _themeService?.CurrentApplicationThemeId,
            AccentColor = _themeService?.AccentColor,
            BackdropType = (int)(_themeService?.CurrentBackdropType ?? WindowBackdropType.Mica),
            OpenPaneLength = _stateService?.OpenPaneLength ?? 0,
            MailListPaneLength = _stateService?.MailListPaneLength ?? 0,
            CalendarDisplayType = (int)(_stateService?.CalendarDisplayType ?? CalendarDisplayType.Week),
            DayDisplayCount = _stateService?.DayDisplayCount ?? 0
        };
    }

    private async Task<List<SnapshotFilter>?> ExportFiltersAsync(List<MailAccount> accounts)
    {
        if (_filterService == null) return null;

        var filters = new List<SnapshotFilter>();
        foreach (var account in accounts)
        {
            // Provider rules live on the server and come back on sync. Only Wino's own rules travel.
            var localFilters = (await _filterService.GetFiltersAsync(account.Id).ConfigureAwait(false))
                .Where(a => a.ManagementType == MailFilterManagementType.WinoLocal && !string.IsNullOrWhiteSpace(a.Name));

            filters.AddRange(localFilters.Select(a => new SnapshotFilter
            {
                AccountAddress = account.Address,
                ProviderType = (int)account.ProviderType,
                Name = a.Name.Trim(),
                SourceRemoteFolderId = a.SourceRemoteFolderId ?? string.Empty,
                MatchMode = (int)a.MatchMode,
                IsEnabled = a.IsEnabled,
                Sequence = a.Sequence,
                StopProcessing = a.StopProcessing,
                Conditions = a.Conditions.OrderBy(c => c.Order).Select(c => new SnapshotFilterCondition { Order = c.Order, Field = (int)c.Field, Operator = (int)c.Operator, Value = c.Value ?? string.Empty }).ToList(),
                Actions = a.Actions.OrderBy(c => c.Order).Select(c => new SnapshotFilterAction { Order = c.Order, Type = (int)c.Type, TargetRemoteFolderId = c.TargetRemoteFolderId }).ToList()
            }));
        }

        return filters;
    }

    private async Task<List<SnapshotCategory>?> ExportCategoriesAsync(List<MailAccount> accounts)
    {
        if (_categoryService == null) return null;

        var categories = new List<SnapshotCategory>();
        foreach (var account in accounts)
        {
            var localCategories = await _categoryService.GetCategoriesAsync(account.Id).ConfigureAwait(false);

            categories.AddRange(localCategories
                .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                .Select(a => new SnapshotCategory
                {
                    AccountAddress = account.Address,
                    ProviderType = (int)account.ProviderType,
                    Name = a.Name.Trim(),
                    IsFavorite = a.IsFavorite,
                    BackgroundColorHex = a.BackgroundColorHex,
                    TextColorHex = a.TextColorHex,
                    Source = (int)a.Source
                }));
        }

        return categories;
    }

    private static List<SnapshotAccountCapabilities> ExportAccountCapabilities(List<MailAccount> accounts)
        => accounts
            .Select(account => new SnapshotAccountCapabilities
            {
                AccountAddress = account.Address,
                ProviderType = (int)account.ProviderType,
                IsCalendarEnabled = account.IsCalendarAccessEnabled,
                CalendarIntegrationSource = (int)account.CalendarIntegrationSource,
                IsContactsEnabled = account.IsContactAccessEnabled,
                ContactIntegrationSource = (int)account.ContactIntegrationSource,
                IsTasksEnabled = account.IsTaskAccessEnabled,
                TaskIntegrationSource = (int)account.TaskIntegrationSource
            })
            .ToList();

    private async Task<List<SnapshotAlias>> ExportAliasesAsync(List<MailAccount> accounts)
    {
        var aliases = new List<SnapshotAlias>();
        foreach (var account in accounts)
        {
            var localAliases = await _accountService.GetAccountAliasesAsync(account.Id).ConfigureAwait(false);

            // The root alias is recreated with the account; provider-discovered ones come back on sync.
            aliases.AddRange(localAliases
                .Where(a => a.Source == AliasSource.Manual && !a.IsRootAlias && !string.IsNullOrWhiteSpace(a.AliasAddress))
                .Select(a => new SnapshotAlias
                {
                    AccountAddress = account.Address,
                    ProviderType = (int)account.ProviderType,
                    AliasAddress = a.AliasAddress.Trim(),
                    ReplyToAddress = a.ReplyToAddress,
                    AliasSenderName = a.AliasSenderName
                }));
        }

        return aliases;
    }

    private static List<SnapshotMergedInbox> ExportMergedInboxes(List<MailAccount> accounts)
        => accounts
            .Where(a => a.MergedInboxId.HasValue && !string.IsNullOrWhiteSpace(a.MergedInbox?.Name))
            .GroupBy(a => a.MergedInboxId!.Value)
            .Select(g => new SnapshotMergedInbox
            {
                Name = g.First().MergedInbox.Name.Trim(),
                Members = g.Select(a => new SnapshotMailboxReference { AccountAddress = a.Address, ProviderType = (int)a.ProviderType }).ToList()
            })
            .ToList();

    #endregion

    #region Import

    private async Task<WinoAccountSyncImportResult> ApplyDocumentAsync(WinoAccountSyncSelection selection, WinoSyncSnapshotDocument document, CancellationToken cancellationToken)
    {
        var hadRemotePreferences = false;
        var appliedPreferenceCount = 0;
        var failedPreferenceCount = 0;
        var importedMailboxCount = 0;
        var skippedDuplicateMailboxCount = 0;
        var remoteMailboxCount = 0;
        var appliedAccountDataCount = 0;
        var appliedFolderConfigurationCount = 0;
        var appliedAppDataCount = 0;

        if (selection.IncludePreferences && !string.IsNullOrWhiteSpace(document.PreferencesJson))
        {
            (appliedPreferenceCount, failedPreferenceCount) = _preferencesService.ImportPreferences(document.PreferencesJson);
            hadRemotePreferences = true;
        }

        if (selection.IncludePreferences)
        {
            appliedAppDataCount += await ApplyTemplatesAsync(document.Templates).ConfigureAwait(false);
            appliedAppDataCount += await ApplyShortcutsAsync(document.Shortcuts).ConfigureAwait(false);
        }

        if (selection.IncludeAccounts)
        {
            var mailboxes = document.Mailboxes ?? [];
            var orderedMailboxes = mailboxes
                .OrderBy(a => a.SortOrder)
                .ThenBy(a => a.Address, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var localAccounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
            var existingKeys = localAccounts
                .Select(CreateMailboxKey)
                .ToHashSet(StringComparer.Ordinal);

            // Account data is applied to every mailbox that resolves to a local account, including the
            // ones skipped as duplicates. Two devices holding the same mailboxes is the common case, and
            // that is exactly when the settings are out of date.
            var accountsByKey = localAccounts.ToDictionary(CreateMailboxKey, StringComparer.Ordinal);

            var capabilitiesByKey = (document.AccountCapabilities ?? [])
                .GroupBy(a => CreateMailboxKey(a.AccountAddress, a.ProviderType), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

            foreach (var mailbox in orderedMailboxes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var mailboxKey = CreateMailboxKey(mailbox.Address, mailbox.ProviderType);
                if (!existingKeys.Add(mailboxKey))
                {
                    skippedDuplicateMailboxCount++;
                    continue;
                }

                var account = CreateImportedAccount(mailbox, capabilitiesByKey.GetValueOrDefault(mailboxKey));
                var serverInformation = CreateImportedServerInformation(mailbox, account.Id);

                await _accountService.CreateAccountAsync(account, serverInformation).ConfigureAwait(false);

                if (account.IsMailAccessGranted)
                {
                    await _accountService.CreateRootAliasAsync(account.Id, account.Address).ConfigureAwait(false);
                }

                if (account.ProviderType is MailProviderType.IMAP4 or MailProviderType.POP3)
                {
                    var persistedAccount = await _accountService.GetAccountAsync(account.Id).ConfigureAwait(false);
                    if (persistedAccount != null && persistedAccount.AttentionReason != AccountAttentionReason.InvalidCredentials)
                    {
                        persistedAccount.AttentionReason = AccountAttentionReason.InvalidCredentials;
                        await _accountService.UpdateAccountAsync(persistedAccount).ConfigureAwait(false);
                    }
                }

                accountsByKey[mailboxKey] = account;
                importedMailboxCount++;
            }

            if (importedMailboxCount > 0)
            {
                WeakReferenceMessenger.Default.Send(new AccountsMenuRefreshRequested(false));
            }

            foreach (var mailbox in orderedMailboxes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!accountsByKey.TryGetValue(CreateMailboxKey(mailbox.Address, mailbox.ProviderType), out var localAccount))
                {
                    continue;
                }

                var applied = await ApplyAccountDataAsync(localAccount, mailbox).ConfigureAwait(false);

                if (applied.AppliedAnything)
                {
                    appliedAccountDataCount++;
                }

                appliedFolderConfigurationCount += applied.AppliedFolderCount;
            }

            remoteMailboxCount = orderedMailboxes.Count;

            appliedAppDataCount += await ApplyFiltersAsync(document.Filters, accountsByKey).ConfigureAwait(false);
            appliedAppDataCount += await ApplyCategoriesAsync(document.Categories, accountsByKey).ConfigureAwait(false);
            appliedAppDataCount += await ApplyAliasesAsync(document.Aliases, accountsByKey).ConfigureAwait(false);
            appliedAppDataCount += await ApplyMergedInboxesAsync(document.MergedInboxes).ConfigureAwait(false);
        }

        await RepairStartupEntityAsync().ConfigureAwait(false);

        return new WinoAccountSyncImportResult
        {
            IncludedPreferences = selection.IncludePreferences,
            IncludedAccounts = selection.IncludeAccounts,
            HadRemotePreferences = hadRemotePreferences,
            AppliedPreferenceCount = appliedPreferenceCount,
            FailedPreferenceCount = failedPreferenceCount,
            ImportedMailboxCount = importedMailboxCount,
            SkippedDuplicateMailboxCount = skippedDuplicateMailboxCount,
            RemoteMailboxCount = remoteMailboxCount,
            AppliedAccountDataCount = appliedAccountDataCount,
            AppliedFolderConfigurationCount = appliedFolderConfigurationCount,
            AppliedAppDataCount = appliedAppDataCount,
            Appearance = selection.IncludePreferences ? document.Appearance : null
        };
    }

    /// <summary>Templates match by name. Existing ones are updated, missing ones created, none deleted.</summary>
    private async Task<int> ApplyTemplatesAsync(List<SnapshotTemplate>? templates)
    {
        if (_templateService == null || templates == null || templates.Count == 0) return 0;

        var applied = 0;
        var local = await _templateService.GetEmailTemplatesAsync().ConfigureAwait(false);

        foreach (var template in templates.Where(a => !string.IsNullOrWhiteSpace(a.Name)))
        {
            try
            {
                var existing = local.FirstOrDefault(a => string.Equals(a.Name?.Trim(), template.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    var created = await _templateService.CreateEmailTemplateAsync(new EmailTemplate
                    {
                        Id = Guid.NewGuid(),
                        Name = template.Name.Trim(),
                        Description = template.Description ?? string.Empty,
                        HtmlContent = template.HtmlContent ?? string.Empty
                    }).ConfigureAwait(false);

                    local.Add(created);
                    applied++;
                }
                else if (!string.Equals(existing.HtmlContent, template.HtmlContent, StringComparison.Ordinal)
                    || !string.Equals(existing.Description, template.Description, StringComparison.Ordinal))
                {
                    existing.HtmlContent = template.HtmlContent ?? string.Empty;
                    existing.Description = template.Description ?? string.Empty;
                    await _templateService.UpdateEmailTemplateAsync(existing).ConfigureAwait(false);
                    applied++;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Template {Name} could not be restored.", template.Name);
            }
        }

        return applied;
    }

    /// <summary>
    /// Shortcuts match by mode and action. A key that is reserved or already taken locally leaves
    /// the local binding alone; only the enabled flag follows the snapshot then.
    /// </summary>
    private async Task<int> ApplyShortcutsAsync(List<SnapshotShortcut>? shortcuts)
    {
        if (_shortcutService == null || shortcuts == null || shortcuts.Count == 0) return 0;

        var applied = 0;
        var local = (await _shortcutService.GetKeyboardShortcutsAsync().ConfigureAwait(false)).ToList();

        foreach (var shortcut in shortcuts.Where(a => !string.IsNullOrWhiteSpace(a.Key)))
        {
            var mode = (WinoApplicationMode)shortcut.Mode;
            var action = (KeyboardShortcutAction)shortcut.Action;
            var modifiers = (ModifierKeys)shortcut.ModifierKeys;
            if (!Enum.IsDefined(mode) || !Enum.IsDefined(action)) continue;

            try
            {
                var existing = local.FirstOrDefault(a => a.Mode == mode && a.Action == action);
                if (existing == null)
                {
                    var candidate = new KeyboardShortcut { Id = Guid.NewGuid(), Mode = mode, Key = shortcut.Key, ModifierKeys = modifiers, Action = action, IsEnabled = shortcut.IsEnabled };
                    if (!_shortcutService.IsShortcutAllowed(candidate)
                        || await _shortcutService.IsKeyCombinationInUseAsync(mode, shortcut.Key, modifiers).ConfigureAwait(false))
                    {
                        continue;
                    }

                    local.Add(await _shortcutService.SaveKeyboardShortcutAsync(candidate).ConfigureAwait(false));
                    applied++;
                    continue;
                }

                var bindingChanged = !string.Equals(existing.Key, shortcut.Key, StringComparison.OrdinalIgnoreCase) || existing.ModifierKeys != modifiers;
                if (bindingChanged
                    && !_shortcutService.IsReservedShortcut(mode, shortcut.Key, modifiers)
                    && !await _shortcutService.IsKeyCombinationInUseAsync(mode, shortcut.Key, modifiers, existing.Id).ConfigureAwait(false))
                {
                    existing.Key = shortcut.Key;
                    existing.ModifierKeys = modifiers;
                    existing.IsEnabled = shortcut.IsEnabled;
                    await _shortcutService.SaveKeyboardShortcutAsync(existing).ConfigureAwait(false);
                    applied++;
                }
                else if (existing.IsEnabled != shortcut.IsEnabled)
                {
                    await _shortcutService.UpdateKeyboardShortcutEnabledAsync(existing.Id, shortcut.IsEnabled).ConfigureAwait(false);
                    applied++;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Shortcut {Action} could not be restored.", action);
            }
        }

        return applied;
    }

    /// <summary>Wino's own rules match by account and name; existing ones are rewritten in place.</summary>
    private async Task<int> ApplyFiltersAsync(List<SnapshotFilter>? filters, Dictionary<string, MailAccount> accountsByKey)
    {
        if (_filterService == null || filters == null || filters.Count == 0) return 0;

        var applied = 0;
        foreach (var group in filters.Where(a => !string.IsNullOrWhiteSpace(a.Name)).GroupBy(a => CreateMailboxKey(a.AccountAddress, a.ProviderType)))
        {
            if (!accountsByKey.TryGetValue(group.Key, out var account)) continue;

            var local = await _filterService.GetFiltersAsync(account.Id).ConfigureAwait(false);

            foreach (var filter in group)
            {
                try
                {
                    var existing = local.FirstOrDefault(a => a.ManagementType == MailFilterManagementType.WinoLocal
                        && string.Equals(a.Name?.Trim(), filter.Name.Trim(), StringComparison.OrdinalIgnoreCase));

                    var target = existing ?? new MailFilter { Id = Guid.NewGuid(), MailAccountId = account.Id, ManagementType = MailFilterManagementType.WinoLocal, IsWinoCreated = true };
                    target.Name = filter.Name.Trim();
                    target.SourceRemoteFolderId = filter.SourceRemoteFolderId;
                    target.MatchMode = (MailFilterMatchMode)filter.MatchMode;
                    target.IsEnabled = filter.IsEnabled;
                    target.Sequence = filter.Sequence;
                    target.StopProcessing = filter.StopProcessing;
                    target.Conditions = filter.Conditions.Select(c => new MailFilterCondition { Order = c.Order, Field = (MailFilterConditionField)c.Field, Operator = (MailFilterConditionOperator)c.Operator, Value = c.Value }).ToList();
                    target.Actions = filter.Actions.Select(c => new MailFilterAction { Order = c.Order, Type = (MailFilterActionType)c.Type, TargetRemoteFolderId = c.TargetRemoteFolderId ?? string.Empty }).ToList();

                    if (existing == null)
                    {
                        local.Add(await _filterService.CreateFilterAsync(target).ConfigureAwait(false));
                    }
                    else
                    {
                        await _filterService.UpdateFilterAsync(target).ConfigureAwait(false);
                    }

                    applied++;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Rule {Name} could not be restored.", filter.Name);
                }
            }
        }

        return applied;
    }

    /// <summary>
    /// Categories match by account and name. Local categories are created; provider categories
    /// come from the server, so only their favourite flag and colours are applied.
    /// </summary>
    private async Task<int> ApplyCategoriesAsync(List<SnapshotCategory>? categories, Dictionary<string, MailAccount> accountsByKey)
    {
        if (_categoryService == null || categories == null || categories.Count == 0) return 0;

        var applied = 0;
        foreach (var group in categories.Where(a => !string.IsNullOrWhiteSpace(a.Name)).GroupBy(a => CreateMailboxKey(a.AccountAddress, a.ProviderType)))
        {
            if (!accountsByKey.TryGetValue(group.Key, out var account)) continue;

            var local = await _categoryService.GetCategoriesAsync(account.Id).ConfigureAwait(false);

            foreach (var category in group)
            {
                try
                {
                    var existing = local.FirstOrDefault(a => string.Equals(a.Name?.Trim(), category.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (existing == null)
                    {
                        if ((MailCategorySource)category.Source != MailCategorySource.Local) continue;

                        local.Add(await _categoryService.CreateCategoryAsync(new MailCategory
                        {
                            Id = Guid.NewGuid(),
                            MailAccountId = account.Id,
                            RemoteId = string.Empty,
                            Name = category.Name.Trim(),
                            IsFavorite = category.IsFavorite,
                            BackgroundColorHex = category.BackgroundColorHex ?? string.Empty,
                            TextColorHex = category.TextColorHex ?? string.Empty,
                            Source = MailCategorySource.Local
                        }).ConfigureAwait(false));
                        applied++;
                        continue;
                    }

                    var changed = existing.IsFavorite != category.IsFavorite
                        || !string.Equals(existing.BackgroundColorHex, category.BackgroundColorHex, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(existing.TextColorHex, category.TextColorHex, StringComparison.OrdinalIgnoreCase);
                    if (!changed) continue;

                    existing.IsFavorite = category.IsFavorite;
                    existing.BackgroundColorHex = category.BackgroundColorHex ?? existing.BackgroundColorHex;
                    existing.TextColorHex = category.TextColorHex ?? existing.TextColorHex;
                    await _categoryService.UpdateCategoryAsync(existing).ConfigureAwait(false);
                    applied++;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Category {Name} could not be restored.", category.Name);
                }
            }
        }

        return applied;
    }

    /// <summary>Manual aliases match by account and address. Missing ones are added.</summary>
    private async Task<int> ApplyAliasesAsync(List<SnapshotAlias>? aliases, Dictionary<string, MailAccount> accountsByKey)
    {
        if (aliases == null || aliases.Count == 0) return 0;

        var applied = 0;
        foreach (var group in aliases.Where(a => !string.IsNullOrWhiteSpace(a.AliasAddress)).GroupBy(a => CreateMailboxKey(a.AccountAddress, a.ProviderType)))
        {
            if (!accountsByKey.TryGetValue(group.Key, out var account)) continue;

            var local = await _accountService.GetAccountAliasesAsync(account.Id).ConfigureAwait(false);
            var localAddresses = local.Select(a => a.AliasAddress?.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

            foreach (var alias in group)
            {
                var address = alias.AliasAddress.Trim().ToLowerInvariant();
                if (!localAddresses.Add(address)) continue;

                try
                {
                    var added = await _accountService.AddAccountAliasAsync(account.Id, new MailAccountAlias
                    {
                        Id = Guid.NewGuid(),
                        AccountId = account.Id,
                        AliasAddress = address,
                        ReplyToAddress = alias.ReplyToAddress ?? string.Empty,
                        AliasSenderName = alias.AliasSenderName ?? string.Empty,
                        Source = AliasSource.Manual
                    }).ConfigureAwait(false);

                    if (added) applied++;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Alias {Address} could not be restored.", address);
                }
            }
        }

        return applied;
    }

    /// <summary>
    /// Merged inboxes match by name. Members are resolved by address and provider; a merged inbox
    /// with fewer than two members here is skipped, and accounts already in another merge stay there.
    /// </summary>
    private async Task<int> ApplyMergedInboxesAsync(List<SnapshotMergedInbox>? mergedInboxes)
    {
        if (mergedInboxes == null || mergedInboxes.Count == 0) return 0;

        var applied = 0;
        foreach (var mergedInbox in mergedInboxes.Where(a => !string.IsNullOrWhiteSpace(a.Name)))
        {
            try
            {
                var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
                var accountsByKey = accounts.ToDictionary(CreateMailboxKey, StringComparer.Ordinal);
                var name = mergedInbox.Name.Trim();

                var members = mergedInbox.Members
                    .Select(m => accountsByKey.GetValueOrDefault(CreateMailboxKey(m.AccountAddress, m.ProviderType)))
                    .Where(a => a != null)
                    .Select(a => a!)
                    .ToList();
                if (members.Count < 2) continue;

                var existing = accounts.FirstOrDefault(a => a.MergedInboxId.HasValue && string.Equals(a.MergedInbox?.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase));
                if (existing?.MergedInboxId is { } existingId)
                {
                    var linked = accounts.Where(a => a.MergedInboxId == existingId).Select(a => a.Id)
                        .Union(members.Where(a => !a.MergedInboxId.HasValue || a.MergedInboxId == existingId).Select(a => a.Id))
                        .Distinct()
                        .ToList();

                    await _accountService.UpdateMergedInboxAsync(existingId, linked).ConfigureAwait(false);
                    applied++;
                    continue;
                }

                var free = members.Where(a => !a.MergedInboxId.HasValue).ToList();
                if (free.Count < 2) continue;

                await _accountService.CreateMergeAccountsAsync(new MergedInbox { Name = name }, free).ConfigureAwait(false);
                applied++;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Merged inbox {Name} could not be restored.", mergedInbox.Name);
            }
        }

        return applied;
    }

    /// <summary>
    /// Applies the per-account preferences, signatures and folder layout carried by a synced mailbox.
    /// Older servers and version 1 export files carry none of this, in which case nothing happens.
    /// </summary>
    private async Task<AppliedAccountData> ApplyAccountDataAsync(MailAccount account, UserMailboxSyncItemDto mailbox)
    {
        var signatureIdMap = await ApplySignaturesAsync(account.Id, mailbox.Signatures).ConfigureAwait(false);
        var appliedPreferences = await ApplyAccountPreferencesAsync(account, mailbox, signatureIdMap).ConfigureAwait(false);
        var appliedFolderCount = await ApplyFolderConfigurationAsync(account.Id, mailbox.Folders).ConfigureAwait(false);

        var appliedAnything = appliedPreferences || signatureIdMap.Count > 0 || appliedFolderCount > 0;

        return new AppliedAccountData(appliedAnything, appliedFolderCount);
    }

    /// <summary>
    /// Matches incoming signatures to local ones by name and returns a source id to local id map so that
    /// the account preference pointers can be translated. Local signatures that are absent from the
    /// payload are never deleted.
    /// </summary>
    private async Task<Dictionary<Guid, Guid>> ApplySignaturesAsync(Guid accountId, List<UserMailboxSignatureSyncItemDto>? signatures)
    {
        var signatureIdMap = new Dictionary<Guid, Guid>();

        if (signatures == null || signatures.Count == 0) return signatureIdMap;

        var localSignatures = await _signatureService.GetSignaturesAsync(accountId).ConfigureAwait(false);

        foreach (var signature in signatures)
        {
            var name = signature.Name?.Trim();
            if (string.IsNullOrEmpty(name)) continue;

            var localSignature = localSignatures.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

            if (localSignature == null)
            {
                localSignature = await _signatureService.CreateSignatureAsync(new AccountSignature
                {
                    Id = Guid.NewGuid(),
                    MailAccountId = accountId,
                    Name = name,
                    HtmlBody = signature.HtmlBody ?? string.Empty
                }).ConfigureAwait(false);
            }
            else if (!string.Equals(localSignature.HtmlBody, signature.HtmlBody, StringComparison.Ordinal))
            {
                localSignature.HtmlBody = signature.HtmlBody ?? string.Empty;
                localSignature = await _signatureService.UpdateSignatureAsync(localSignature).ConfigureAwait(false);
            }

            if (localSignature != null)
            {
                signatureIdMap[signature.Id] = localSignature.Id;
            }
        }

        return signatureIdMap;
    }

    private async Task<bool> ApplyAccountPreferencesAsync(
        MailAccount account,
        UserMailboxSyncItemDto mailbox,
        Dictionary<Guid, Guid> signatureIdMap)
    {
        var persistedAccount = await _accountService.GetAccountAsync(account.Id).ConfigureAwait(false);
        var preferences = persistedAccount?.Preferences;

        if (preferences == null) return false;

        var hasChanges = false;

        hasChanges |= AssignIfProvided(mailbox.ShouldAppendMessagesToSentFolder, preferences.ShouldAppendMessagesToSentFolder,
            value => preferences.ShouldAppendMessagesToSentFolder = value);
        hasChanges |= AssignIfProvided(mailbox.IsNotificationsEnabled, preferences.IsNotificationsEnabled,
            value => preferences.IsNotificationsEnabled = value);
        hasChanges |= AssignIfProvided(mailbox.IsSignatureEnabled, preferences.IsSignatureEnabled,
            value => preferences.IsSignatureEnabled = value);
        hasChanges |= AssignIfProvided(mailbox.IsTaskbarBadgeEnabled, preferences.IsTaskbarBadgeEnabled,
            value => preferences.IsTaskbarBadgeEnabled = value);
        hasChanges |= AssignIfProvided(mailbox.IsJumpListEnabled, preferences.IsJumpListEnabled,
            value => preferences.IsJumpListEnabled = value);

        if (mailbox.IsFocusedInboxEnabled != preferences.IsFocusedInboxEnabled && mailbox.IsFocusedInboxEnabled.HasValue)
        {
            preferences.IsFocusedInboxEnabled = mailbox.IsFocusedInboxEnabled;
            hasChanges = true;
        }

        // Signature pointers reference ids from the exporting device. A pointer that cannot be
        // resolved against the freshly matched local signatures is dropped instead of dangling.
        hasChanges |= AssignSignaturePointer(mailbox.SignatureIdForNewMessages, signatureIdMap,
            preferences.SignatureIdForNewMessages, value => preferences.SignatureIdForNewMessages = value);
        hasChanges |= AssignSignaturePointer(mailbox.SignatureIdForFollowingMessages, signatureIdMap,
            preferences.SignatureIdForFollowingMessages, value => preferences.SignatureIdForFollowingMessages = value);

        if (!hasChanges) return false;

        await _accountService.UpdateAccountPreferencesAsync(preferences).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Applies the folder layout to folders that already exist, and parks the rest for the synchronizers.
    /// Returns how many entries were applied directly.
    /// </summary>
    private async Task<int> ApplyFolderConfigurationAsync(Guid accountId, List<UserMailboxFolderSyncItemDto>? folders)
    {
        if (folders == null || folders.Count == 0) return 0;

        var appliedFolderCount = 0;

        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder.RemoteFolderId)) continue;

            var remoteFolderId = folder.RemoteFolderId.Trim();
            var localFolder = await _folderService.GetFolderAsync(accountId, remoteFolderId).ConfigureAwait(false);

            if (localFolder == null)
            {
                // The account was just imported and has no folders until it is re-authenticated
                // and synchronized. Park the layout until the folder arrives.
                await _folderService.UpsertFolderConfigurationOverrideAsync(new FolderConfigurationOverride
                {
                    MailAccountId = accountId,
                    RemoteFolderId = remoteFolderId,
                    IsSticky = folder.IsSticky,
                    IsHidden = folder.IsHidden,
                    Order = folder.Order,
                    ShowUnreadCount = folder.ShowUnreadCount,
                    IsCountedInAccountTotal = folder.ShowUnreadCount,
                    IsJumpListEnabled = folder.IsJumpListEnabled
                }).ConfigureAwait(false);

                continue;
            }

            localFolder.IsSticky = folder.IsSticky;
            localFolder.IsHidden = folder.IsHidden;
            localFolder.Order = folder.Order;
            localFolder.ShowUnreadCount = folder.ShowUnreadCount;
            localFolder.IsJumpListEnabled = folder.IsJumpListEnabled;

            await _folderService.UpdateFolderAsync(localFolder).ConfigureAwait(false);

            appliedFolderCount++;
        }

        if (appliedFolderCount > 0)
        {
            // The bulk path has to announce the change itself. The single-folder mutators on
            // IFolderService do not all broadcast, so the shell would keep the stale layout.
            WeakReferenceMessenger.Default.Send(new AccountFolderConfigurationUpdated(accountId));
        }

        return appliedFolderCount;
    }

    private static bool AssignIfProvided(bool? incomingValue, bool currentValue, Action<bool> assign)
    {
        if (!incomingValue.HasValue || incomingValue.Value == currentValue) return false;

        assign(incomingValue.Value);

        return true;
    }

    private static bool AssignSignaturePointer(
        Guid? incomingSignatureId,
        Dictionary<Guid, Guid> signatureIdMap,
        Guid? currentSignatureId,
        Action<Guid?> assign)
    {
        if (!incomingSignatureId.HasValue) return false;

        if (!signatureIdMap.TryGetValue(incomingSignatureId.Value, out var localSignatureId)) return false;

        if (currentSignatureId == localSignatureId) return false;

        assign(localSignatureId);

        return true;
    }

    #endregion

    #region Mailbox mapping

    private async Task<UserMailboxSyncItemDto> MapMailboxAsync(MailAccount account)
    {
        var serverInformation = account.ProviderType is MailProviderType.IMAP4 or MailProviderType.POP3
            ? account.ServerInformation
            : null;

        var preferences = account.Preferences;
        var signatures = await _signatureService.GetSignaturesAsync(account.Id).ConfigureAwait(false);
        var folders = await _folderService.GetFoldersAsync(account.Id).ConfigureAwait(false);

        return new UserMailboxSyncItemDto
        {
            Address = account.Address ?? string.Empty,
            ProviderType = (int)account.ProviderType,
            SpecialImapProvider = (int)account.SpecialImapProvider,
            AccountName = account.Name,
            SenderName = account.SenderName,
            AccountColorHex = account.AccountColorHex,
            SortOrder = account.Order,
            IsCalendarAccessGranted = account.IsCalendarAccessGranted,
            CalendarSupportMode = serverInformation != null ? (int)serverInformation.CalendarSupportMode : 0,
            IncomingServer = serverInformation?.IncomingServer,
            IncomingServerPort = serverInformation?.IncomingServerPort,
            IncomingServerUsername = serverInformation?.IncomingServerUsername,
            IncomingServerSocketOption = serverInformation != null ? (int?)serverInformation.IncomingServerSocketOption : null,
            IncomingAuthenticationMethod = serverInformation != null ? (int?)serverInformation.IncomingAuthenticationMethod : null,
            OutgoingServer = serverInformation?.OutgoingServer,
            OutgoingServerPort = serverInformation?.OutgoingServerPort,
            OutgoingServerUsername = serverInformation?.OutgoingServerUsername,
            OutgoingServerSocketOption = serverInformation != null ? (int?)serverInformation.OutgoingServerSocketOption : null,
            OutgoingAuthenticationMethod = serverInformation != null ? (int?)serverInformation.OutgoingAuthenticationMethod : null,
            CalDavServiceUrl = serverInformation?.CalDavServiceUrl,
            CalDavUsername = serverInformation?.CalDavUsername,
            ProxyServer = serverInformation?.ProxyServer,
            ProxyServerPort = serverInformation?.ProxyServerPort,
            MaxConcurrentClients = serverInformation?.MaxConcurrentClients,
            IsMailAccessGranted = account.IsMailAccessGranted,

            // Intelligence preferences (daily briefing, semantic indexing) are intentionally not synced.
            // They must be enabled explicitly on every device.
            ShouldAppendMessagesToSentFolder = preferences?.ShouldAppendMessagesToSentFolder,
            IsNotificationsEnabled = preferences?.IsNotificationsEnabled,
            IsFocusedInboxEnabled = preferences?.IsFocusedInboxEnabled,
            IsSignatureEnabled = preferences?.IsSignatureEnabled,
            IsTaskbarBadgeEnabled = preferences?.IsTaskbarBadgeEnabled,
            IsJumpListEnabled = preferences?.IsJumpListEnabled,
            SignatureIdForNewMessages = preferences?.SignatureIdForNewMessages,
            SignatureIdForFollowingMessages = preferences?.SignatureIdForFollowingMessages,
            Signatures = signatures
                .Select(a => new UserMailboxSignatureSyncItemDto
                {
                    Id = a.Id,
                    Name = a.Name ?? string.Empty,
                    HtmlBody = a.HtmlBody ?? string.Empty
                })
                .ToList(),
            Folders = folders
                .Where(a => !string.IsNullOrEmpty(a.RemoteFolderId))
                .Select(a => new UserMailboxFolderSyncItemDto
                {
                    RemoteFolderId = a.RemoteFolderId,
                    IsSticky = a.IsSticky,
                    IsHidden = a.IsHidden,
                    Order = a.Order,
                    ShowUnreadCount = a.ShowUnreadCount,
                    IsJumpListEnabled = a.IsJumpListEnabled
                })
                .ToList()
        };
    }

    private static MailAccount CreateImportedAccount(UserMailboxSyncItemDto mailbox, SnapshotAccountCapabilities? capabilities)
    {
        var providerType = (MailProviderType)mailbox.ProviderType;

        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Address = mailbox.Address.Trim(),
            Name = string.IsNullOrWhiteSpace(mailbox.AccountName) ? mailbox.Address.Trim() : mailbox.AccountName.Trim(),
            SenderName = string.IsNullOrWhiteSpace(mailbox.SenderName) ? mailbox.Address.Trim() : mailbox.SenderName.Trim(),
            ProviderType = providerType,
            SpecialImapProvider = (SpecialImapProvider)mailbox.SpecialImapProvider,
            AccountColorHex = mailbox.AccountColorHex?.Trim() ?? string.Empty,
            Base64ProfilePictureData = string.Empty,
            CreatedAt = DateTime.UtcNow,
            InitialSynchronizationRange = InitialSynchronizationRange.SixMonths,
            IsMailAccessGranted = mailbox.IsMailAccessGranted ?? true,
            SynchronizationDeltaIdentifier = string.Empty,
            CalendarSynchronizationDeltaIdentifier = string.Empty,
            AttentionReason = AccountAttentionReason.InvalidCredentials
        };

        ApplyImportedCapabilities(account, mailbox, capabilities);

        return account;
    }

    /// <summary>
    /// Restores the modes the user had turned on. All three flags of each mode are set together.
    /// Provider modes of an OAuth mailbox are restored as granted but awaiting re-authorization,
    /// the same state a failed token refresh leaves behind, so nothing syncs and no local store
    /// is created before Fix account signs in again with exactly these modes.
    /// </summary>
    private static void ApplyImportedCapabilities(MailAccount account, UserMailboxSyncItemDto mailbox, SnapshotAccountCapabilities? capabilities)
    {
        var isOAuthProvider = account.ProviderType is MailProviderType.Gmail or MailProviderType.Outlook;

        if (capabilities == null)
        {
            // Snapshots without the capability section record only calendar consent. Contacts and
            // To Do stay off, matching the account defaults of the 2.x database migration.
            var calendarSource = !mailbox.IsCalendarAccessGranted
                ? AccountIntegrationSource.Local
                : isOAuthProvider
                    ? AccountIntegrationSource.Provider
                    : account.ProviderType == MailProviderType.IMAP4 && !string.IsNullOrWhiteSpace(mailbox.CalDavServiceUrl)
                        ? AccountIntegrationSource.Dav
                        : AccountIntegrationSource.Local;

            capabilities = new SnapshotAccountCapabilities
            {
                IsCalendarEnabled = mailbox.IsCalendarAccessGranted,
                CalendarIntegrationSource = (int)calendarSource,
                IsContactsEnabled = false,
                ContactIntegrationSource = (int)(isOAuthProvider ? AccountIntegrationSource.Provider : AccountIntegrationSource.Local),
                IsTasksEnabled = false,
                TaskIntegrationSource = (int)(isOAuthProvider ? AccountIntegrationSource.Provider : AccountIntegrationSource.Local)
            };
        }

        var calendarIntegrationSource = ToIntegrationSource(capabilities.CalendarIntegrationSource);
        account.IsCalendarAccessEnabled = capabilities.IsCalendarEnabled;
        account.CalendarIntegrationSource = calendarIntegrationSource;
        account.IsCalendarAccessGranted = capabilities.IsCalendarEnabled && IsRemoteSource(isOAuthProvider, calendarIntegrationSource);

        var contactIntegrationSource = ToIntegrationSource(capabilities.ContactIntegrationSource);
        var isRemoteContacts = capabilities.IsContactsEnabled && IsRemoteSource(isOAuthProvider, contactIntegrationSource);
        account.IsContactAccessEnabled = capabilities.IsContactsEnabled;
        account.ContactIntegrationSource = contactIntegrationSource;
        account.IsContactAccessGranted = isRemoteContacts;
        account.IsContactReauthorizationRequired = isRemoteContacts && isOAuthProvider;

        var taskIntegrationSource = ToIntegrationSource(capabilities.TaskIntegrationSource);
        var isRemoteTasks = capabilities.IsTasksEnabled && IsRemoteSource(isOAuthProvider, taskIntegrationSource);
        account.IsTaskAccessEnabled = capabilities.IsTasksEnabled;
        account.TaskIntegrationSource = taskIntegrationSource;
        account.IsTaskAccessGranted = isRemoteTasks;
        account.IsTaskReauthorizationRequired = isRemoteTasks && isOAuthProvider;
    }

    /// <summary>
    /// A local-backed mode is never granted and never waits for consent. OAuth mailboxes only
    /// sign in to their own provider; custom mailboxes use DAV.
    /// </summary>
    private static bool IsRemoteSource(bool isOAuthProvider, AccountIntegrationSource source)
        => isOAuthProvider
            ? source == AccountIntegrationSource.Provider
            : source != AccountIntegrationSource.Local;

    private static AccountIntegrationSource ToIntegrationSource(int value)
        => Enum.IsDefined((AccountIntegrationSource)value)
            ? (AccountIntegrationSource)value
            : AccountIntegrationSource.Local;

    private static CustomServerInformation? CreateImportedServerInformation(UserMailboxSyncItemDto mailbox, Guid accountId)
    {
        var providerType = (MailProviderType)mailbox.ProviderType;
        if (providerType is not (MailProviderType.IMAP4 or MailProviderType.POP3))
        {
            return null;
        }

        return new CustomServerInformation
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Address = mailbox.Address.Trim(),
            IncomingServer = mailbox.IncomingServer?.Trim() ?? string.Empty,
            IncomingServerPort = mailbox.IncomingServerPort?.Trim() ?? string.Empty,
            IncomingServerUsername = mailbox.IncomingServerUsername?.Trim() ?? string.Empty,
            IncomingServerPassword = string.Empty,
            IncomingServerType = providerType == MailProviderType.POP3
                ? CustomIncomingServerType.POP3
                : CustomIncomingServerType.IMAP4,
            IncomingServerSocketOption = mailbox.IncomingServerSocketOption is int incomingSocketOption
                ? (ImapConnectionSecurity)incomingSocketOption
                : ImapConnectionSecurity.Auto,
            IncomingAuthenticationMethod = mailbox.IncomingAuthenticationMethod is int incomingAuthMethod
                ? (ImapAuthenticationMethod)incomingAuthMethod
                : ImapAuthenticationMethod.Auto,
            OutgoingServer = mailbox.OutgoingServer?.Trim() ?? string.Empty,
            OutgoingServerPort = mailbox.OutgoingServerPort?.Trim() ?? string.Empty,
            OutgoingServerUsername = mailbox.OutgoingServerUsername?.Trim() ?? string.Empty,
            OutgoingServerPassword = string.Empty,
            OutgoingServerSocketOption = mailbox.OutgoingServerSocketOption is int outgoingSocketOption
                ? (ImapConnectionSecurity)outgoingSocketOption
                : ImapConnectionSecurity.Auto,
            OutgoingAuthenticationMethod = mailbox.OutgoingAuthenticationMethod is int outgoingAuthMethod
                ? (ImapAuthenticationMethod)outgoingAuthMethod
                : ImapAuthenticationMethod.Auto,
            CalDavServiceUrl = mailbox.CalDavServiceUrl?.Trim() ?? string.Empty,
            CalDavUsername = mailbox.CalDavUsername?.Trim() ?? string.Empty,
            CalDavPassword = string.Empty,
            CalendarSupportMode = (ImapCalendarSupportMode)mailbox.CalendarSupportMode,
            ProxyServer = mailbox.ProxyServer?.Trim() ?? string.Empty,
            ProxyServerPort = mailbox.ProxyServerPort?.Trim() ?? string.Empty,
            MaxConcurrentClients = mailbox.MaxConcurrentClients.GetValueOrDefault(DefaultMaxConcurrentClients),
            ConnectionPolicyVersion = ImapConnectionPolicyVersion.Legacy
        };
    }

    private async Task RepairStartupEntityAsync()
    {
        if (!_preferencesService.StartupEntityId.HasValue)
        {
            return;
        }

        var startupEntityId = _preferencesService.StartupEntityId.Value;
        var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
        var accountIds = accounts.Select(a => a.Id);
        var mergedInboxIds = accounts.Where(a => a.MergedInboxId.HasValue).Select(a => a.MergedInboxId!.Value);

        if (accountIds.Concat(mergedInboxIds).Contains(startupEntityId))
        {
            return;
        }

        _preferencesService.StartupEntityId = accounts.FirstOrDefault()?.Id;
    }

    private static string CreateMailboxKey(MailAccount account)
        => CreateMailboxKey(account.Address, (int)account.ProviderType);

    private static string CreateMailboxKey(string? address, int providerType)
        => $"{address?.Trim().ToLowerInvariant()}|{providerType}";


    #endregion

    private sealed record PreparedSyncExport(WinoSyncSnapshotDocument Document, WinoAccountSyncExportResult ExportResult);

    private readonly record struct AppliedAccountData(bool AppliedAnything, int AppliedFolderCount);
}
