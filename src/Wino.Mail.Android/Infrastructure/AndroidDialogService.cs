using System.Collections.ObjectModel;
using Android.App;
using Android.Content;
using Android.Text;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using Google.Android.Material.CheckBox;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.Snackbar;
using Google.Android.Material.TextField;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Common;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using AlertDialog = AndroidX.AppCompat.App.AlertDialog;

namespace Wino.Mail.Android.Infrastructure;

/// <summary>
/// Material dialogs for the shared dialog contract (UIKitDialogService and the Mac AppKitDialogService):
/// MaterialAlertDialogBuilder for prompts, text input and simple pickers, a Snackbar for info-bar messages and a
/// progress dialog while an account is added. Buttons name their consequence; the positive button is the requested
/// action. A dialog dismissed by the system (for example when the activity is recreated) answers like Cancel.
/// The form dialogs (alias, category, signature, account order, system folders) and the Wino Account dialogs are in the
/// partial files beside this one (T9). File picking uses the document picker (T8); folder and save pickers arrive with the items that use
/// them and until then show the "not implemented yet" message and answer like Cancel.
/// </summary>
public sealed partial class AndroidDialogService(IDispatcher dispatcher, AndroidActivityTracker activities, IServiceProvider services) : IMailDialogService
{
    private readonly SemaphoreSlim _presentations = new(1, 1);

    #region Presentation

    /// <summary>Runs one dialog at a time on the current activity; <paramref name="unavailable"/> without one.</summary>
    private async Task<T> PresentAsync<T>(Func<AppCompatActivity, TaskCompletionSource<T>, Dialog> build, T unavailable)
    {
        await _presentations.WaitAsync();
        try
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            await dispatcher.ExecuteOnUIThread(() =>
            {
                if (activities.Current is not AppCompatActivity { IsFinishing: false, IsDestroyed: false } activity)
                {
                    Serilog.Log.Warning("A dialog was requested while no activity is on screen.");
                    completion.TrySetResult(unavailable);
                    return;
                }
                var dialog = build(activity, completion);
                dialog.DismissEvent += (_, _) => completion.TrySetResult(unavailable);
                dialog.Show();
            });
            return await completion.Task;
        }
        finally { _presentations.Release(); }
    }

    /// <summary>A prompt: 0 for the positive button, 1 for the neutral one, -1 for the negative button or dismissal.</summary>
    private Task<int> AlertAsync(string? title, string? message, string positive, string? negative = null, string? neutral = null,
        Func<Context, View>? content = null, Action<int>? chosen = null, bool cancelable = true)
        => PresentAsync<int>((activity, completion) =>
        {
            var builder = new MaterialAlertDialogBuilder(activity);
            if (!string.IsNullOrEmpty(title)) builder.SetTitle(title);
            if (!string.IsNullOrEmpty(message)) builder.SetMessage(message);
            if (content is not null) builder.SetView(content(activity));
            builder.SetPositiveButton(positive, (_, _) => { chosen?.Invoke(0); completion.TrySetResult(0); });
            if (!string.IsNullOrEmpty(neutral)) builder.SetNeutralButton(neutral, (_, _) => { chosen?.Invoke(1); completion.TrySetResult(1); });
            if (!string.IsNullOrEmpty(negative)) builder.SetNegativeButton(negative, (_, _) => completion.TrySetResult(-1));
            builder.SetCancelable(cancelable);
            return builder.Create();
        }, -1);

    /// <summary>A single-choice list; null when cancelled. Items that cannot be chosen say why in a message.</summary>
    private Task<T?> PickAsync<T>(string title, IReadOnlyList<(T Item, string Text, string? Invalid)> items) where T : class
        => PresentAsync<T?>((activity, completion) =>
        {
            var labels = items.Select(item => item.Invalid is null ? item.Text : $"{item.Text}  ·  {item.Invalid}").ToArray();
            var builder = new MaterialAlertDialogBuilder(activity)
                .SetTitle(title)
                .SetItems(labels, (_, args) =>
                {
                    var picked = items[args.Which];
                    if (picked.Invalid is null) completion.TrySetResult(picked.Item);
                    else
                    {
                        completion.TrySetResult(null);
                        InfoBarMessage(picked.Text, picked.Invalid, InfoBarMessageType.Warning);
                    }
                })
                .SetNegativeButton(Translator.Buttons_Cancel, (_, _) => completion.TrySetResult(null));
            return builder.Create();
        }, null);

    private static int Dp(Context context, int value)
        => (int)Math.Round(value * (context.Resources?.DisplayMetrics?.Density ?? 1f));

    /// <summary>Content padded like a Material dialog body.</summary>
    private static LinearLayout Body(Context context)
    {
        var layout = new LinearLayout(context) { Orientation = Orientation.Vertical };
        layout.SetPadding(Dp(context, 24), Dp(context, 8), Dp(context, 24), 0);
        return layout;
    }

    #endregion

    #region Prompts

    public async Task<bool> ShowConfirmationDialogAsync(string question, string title, string confirmationButtonTitle)
        => await AlertAsync(title, question, confirmationButtonTitle, Translator.Buttons_Cancel) == 0;

    public async Task ShowMessageAsync(string message, string title, WinoCustomMessageDialogIcon icon)
        => await AlertAsync(title, message, Translator.Buttons_OK);

    public async Task<ThreeButtonDialogResult> ShowThreeButtonDialogAsync(string title, string description, string primaryButtonText, string secondaryButtonText, string cancelButtonText, WinoCustomMessageDialogIcon? icon = null)
        => await AlertAsync(title, description, primaryButtonText, cancelButtonText, secondaryButtonText) switch
        {
            0 => ThreeButtonDialogResult.Primary,
            1 => ThreeButtonDialogResult.Secondary,
            _ => ThreeButtonDialogResult.Cancel
        };

    /// <summary>Same contract as Windows: a key already marked "don't ask again" answers false without showing.</summary>
    public async Task<bool> ShowWinoCustomMessageDialogAsync(string title, string description, string approveButtonText, WinoCustomMessageDialogIcon? icon, string cancelButtonText = "", string dontAskAgainConfigurationKey = "")
    {
        var configuration = services.GetService<IConfigurationService>();
        var dontAskKey = string.IsNullOrEmpty(dontAskAgainConfigurationKey) || configuration is null ? null : dontAskAgainConfigurationKey;
        if (dontAskKey is not null && configuration is not null && configuration.Get(dontAskKey, false)) return false;

        MaterialCheckBox? dontAsk = null;
        Func<Context, View>? content = dontAskKey is null ? null : context =>
        {
            var body = Body(context);
            dontAsk = new MaterialCheckBox(context) { Text = Translator.Dialog_DontAskAgain };
            body.AddView(dontAsk);
            return body;
        };
        var result = await AlertAsync(title, description, approveButtonText, string.IsNullOrEmpty(cancelButtonText) ? null : cancelButtonText, content: content);
        if (dontAskKey is not null && configuration is not null && dontAsk is { Checked: true }) configuration.Set(dontAskKey, true);
        return result == 0;
    }

    public Task<bool> ShowHardDeleteConfirmationAsync()
        => ShowWinoCustomMessageDialogAsync(Translator.DialogMessage_HardDeleteConfirmationMessage, Translator.DialogMessage_HardDeleteConfirmationTitle,
            Translator.Buttons_Yes, WinoCustomMessageDialogIcon.Warning, Translator.Buttons_No);

    /// <summary>An empty string when cancelled (callers test for empty input).</summary>
    public async Task<string> ShowTextInputDialogAsync(string currentInput, string dialogTitle, string dialogDescription, string primaryButtonText)
    {
        TextInputEditText? field = null;
        string input = string.Empty;
        var choice = await AlertAsync(dialogTitle, dialogDescription, primaryButtonText, Translator.Buttons_Cancel,
            content: context =>
            {
                var body = Body(context);
                var layout = new TextInputLayout(context);
                field = new TextInputEditText(context) { Text = currentInput ?? string.Empty };
                field.SetSingleLine(true);
                layout.AddView(field);
                body.AddView(layout);
                return body;
            },
            chosen: _ => input = field?.Text ?? string.Empty);
        return choice == 0 ? input : string.Empty;
    }

    public Task<bool> ShowWinoAccountExportDialogAsync() => ShowConfirmationDialogAsync(
        $"{Translator.WinoAccount_Management_ExportDialog_Confirmation}\n\n{Translator.WinoAccount_Management_ExportDialog_EncryptionNotice}\n\n{Translator.WinoAccount_Management_ExportDialog_AccountsDisclaimer} {Translator.WinoAccount_Management_ExportDialog_AccountsRelogin_Device}",
        Translator.WinoAccount_Management_ExportDialog_Title, Translator.WinoAccount_Management_LocalDataExportAction);

    /// <summary>The error, the end of the protocol log, Close and Copy diagnostics (Windows ImapValidationFailedDialog).</summary>
    public async Task ShowImapValidationFailedDialogAsync(string errorMessage, string protocolLog)
    {
        var log = protocolLog ?? string.Empty;
        var shown = log.Length > 1500 ? "…" + log[^1500..] : log;
        var message = string.IsNullOrWhiteSpace(shown) ? errorMessage : $"{errorMessage}\n\n{Translator.ImapValidationFailedDialog_ProtocolLog}\n{shown}";
        var choice = await AlertAsync(Translator.ImapValidationFailedDialog_Title, message, Translator.Buttons_Close, neutral: Translator.ImapValidationFailedDialog_Copy);
        if (choice == 1) await CopyAsync($"{errorMessage}{Environment.NewLine}{Environment.NewLine}{log}");
    }

    /// <summary>Fails closed: Cancel or dismissal answers false.</summary>
    public async Task<bool> ShowServerCertificateTrustDialogAsync(string summary, byte[] certificateRawData)
        => await AlertAsync(Translator.IMAPSetupDialog_CertificateTrustTitle, summary, Translator.Buttons_Trust, Translator.Buttons_Cancel) == 0;

    public async Task ShowMessageSourceDialogAsync(string messageSource)
    {
        var choice = await AlertAsync(Translator.MessageSourceDialog_Title, null, Translator.Buttons_Close, neutral: Translator.Buttons_Copy,
            content: context =>
            {
                var scroll = new ScrollView(context);
                var text = new TextView(context) { Text = messageSource ?? string.Empty };
                text.SetTextIsSelectable(true);
                text.SetPadding(Dp(context, 24), Dp(context, 8), Dp(context, 24), 0);
                scroll.AddView(text);
                return scroll;
            });
        if (choice != 1) return;
        await CopyAsync(messageSource ?? string.Empty);
        InfoBarMessage(Translator.MessageSourceDialog_Title, Translator.MessageSourceDialog_Copied, InfoBarMessageType.Success);
    }

    private Task CopyAsync(string text)
        => services.GetService<IClipboardService>()?.CopyTextAsync(text) ?? Task.CompletedTask;

    #endregion

    #region Info bar

    public void InfoBarMessage(string title, string message, InfoBarMessageType messageType) => ShowSnackbar(title, message, null, null);

    public void InfoBarMessage(string title, string message, InfoBarMessageType messageType, string actionButtonText, Action action)
        => ShowSnackbar(title, message, actionButtonText, action);

    public void ShowNotSupportedMessage()
        => ShowSnackbar(Translator.Info_UnsupportedFunctionalityTitle, Translator.Info_UnsupportedFunctionalityDescription, null, null);

    public void ShowReadOnlyCalendarMessage()
        => ShowSnackbar(Translator.CalendarReadOnly_Title, Translator.CalendarReadOnly_Message, null, null);

    /// <summary>The Windows ShellInfoBar as a snackbar: title and message, an optional action, a longer time with an action.</summary>
    private async void ShowSnackbar(string title, string message, string? actionTitle, Action? action)
    {
        try
        {
            await dispatcher.ExecuteOnUIThread(() =>
            {
                if (activities.Current is not { IsFinishing: false } activity) return;
                var anchor = activity.FindViewById(Resource.Id.wino_root) ?? activity.Window?.DecorView;
                if (anchor is null) return;
                var text = string.IsNullOrWhiteSpace(title) ? message : string.IsNullOrWhiteSpace(message) ? title : $"{title}\n{message}";
                var snackbar = Snackbar.Make(anchor, text, actionTitle is null ? Snackbar.LengthLong : 8000);
                if (snackbar.View.FindViewById<TextView>(Resource.Id.snackbar_text) is { } label) label.SetMaxLines(5);
                if (actionTitle is not null && action is not null) snackbar.SetAction(actionTitle, _ => action());
                snackbar.Show();
            });
        }
        catch (Exception exception) { Serilog.Log.Warning(exception, "Could not show an info bar message."); }
    }

    #endregion

    #region Pickers

    public async Task<IMailItemFolder?> ShowMoveMailFolderDialogAsync(List<IMailItemFolder> availableFolders)
    {
        var rows = new List<(IMailItemFolder, string, string?)>();
        void Add(IEnumerable<IMailItemFolder> folders, int depth)
        {
            foreach (var folder in folders)
            {
                var name = folder.FolderName ?? string.Empty;
                rows.Add((folder, new string(' ', depth * 4) + name,
                    folder.IsMoveTarget ? null : string.Format(Translator.MoveMailDialog_InvalidFolderMessage, name)));
                Add(folder.ChildFolders ?? [], depth + 1);
            }
        }
        Add(availableFolders ?? [], 0);
        return await PickAsync(Translator.MoveMailDialog_Title, rows);
    }

    public async Task<IMailItemFolder?> PickFolderAsync(Guid accountId, PickFolderReason reason, IFolderService folderService)
    {
        var structure = await folderService.GetFolderStructureForAccountAsync(accountId, false);
        return await ShowMoveMailFolderDialogAsync(structure.Folders);
    }

    public Task<MailAccount?> ShowAccountPickerDialogAsync(List<MailAccount> availableAccounts)
        => PickAsync(Translator.AccountPickerDialog_Title, (availableAccounts ?? []).Select(account => (account, AccountLine(account), (string?)null)).ToList());

    public async Task<AccountCalendarPickingResult> ShowSingleCalendarPickerDialogAsync(List<CalendarPickerAccountGroup> availableCalendarGroups)
    {
        var rows = (availableCalendarGroups ?? []).SelectMany(group => (group.Calendars ?? []).Select(calendar =>
            (calendar, $"{calendar.Name} — {AccountLine(group.Account)}", calendar.IsReadOnly ? Translator.CalendarPicker_ReadOnly : (string?)null))).ToList();
        var picked = await PickAsync(Translator.CalendarEventCompose_PickCalendarTitle, rows);
        return new AccountCalendarPickingResult(picked, false);
    }

    public Task<ContactCreateDestination?> ShowContactDestinationPickerDialogAsync(IReadOnlyList<ContactCreateDestination> destinations)
        => PickAsync(Translator.PeopleSettings_DestinationPicker_Title, (destinations ?? []).Select(destination =>
            (destination, destination.DisplayName, destination.IsReadOnly ? Translator.CalendarPicker_ReadOnly : (string?)null)).ToList());

    public Task<AccountTaskList?> ShowTaskListPickerDialogAsync(IReadOnlyList<AccountTaskList> taskLists, IReadOnlyList<MailAccount> accounts)
    {
        var owners = (accounts ?? []).ToDictionary(account => account.Id);
        return PickAsync(Translator.ToDoPage_Title, (taskLists ?? []).Select(list =>
            (list, owners.TryGetValue(list.MailAccountId, out var owner) && !string.IsNullOrWhiteSpace(owner.Name) ? $"{list.Title} — {owner.Name}" : list.Title ?? string.Empty,
             (string?)null)).ToList());
    }

    /// <summary>The list name, then its account from a picker (Windows NewContactListDialog).</summary>
    public async Task<ContactListCreationResult?> ShowNewContactListDialogAsync(IReadOnlyList<MailAccount> accounts, MailAccount? selectedAccount)
    {
        var name = (await ShowTextInputDialogAsync(string.Empty, Translator.ContactList_NewTitle, Translator.ContactList_NameHeader, Translator.Buttons_Create)).Trim();
        if (name.Length == 0) return null;
        var available = (accounts ?? []).ToList();
        var owner = available.Count == 1 ? available[0] : selectedAccount ?? await ShowAccountPickerDialogAsync(available);
        return owner is null ? null : new ContactListCreationResult(name, owner);
    }

    /// <summary>"Name · address", or whichever of the two exists.</summary>
    private static string AccountLine(MailAccount? account)
    {
        var name = account?.Name?.Trim() ?? string.Empty;
        var address = account?.Address?.Trim() ?? string.Empty;
        if (name.Length == 0) return address;
        if (address.Length == 0 || string.Equals(name, address, StringComparison.OrdinalIgnoreCase)) return name;
        return $"{name} · {address}";
    }

    #endregion

    #region Account setup

    public IAccountCreationDialog GetAccountCreationDialog(AccountCreationDialogResult accountCreationDialogResult) => new AccountProgress(dispatcher, activities);

    /// <summary>Not called by shared code: account creation goes through the onboarding provider page.</summary>
    public Task<AccountCreationDialogResult> ShowAccountProviderSelectionDialogAsync(List<IProviderDetail> availableProviders)
        => Task.FromException<AccountCreationDialogResult>(new NotSupportedException("Android adds accounts through the provider selection page."));

    /// <summary>The "adding account" dialog while sign-in and setup run. It never holds the presentation gate.</summary>
    private sealed class AccountProgress(IDispatcher dispatcher, AndroidActivityTracker activities) : IAccountCreationDialog
    {
        private Dialog? _dialog;
        public AccountCreationDialogState State { get; set; }

        public Task ShowDialogAsync(CancellationTokenSource cancellationTokenSource) => dispatcher.ExecuteOnUIThread(() =>
        {
            if (activities.Current is not { IsFinishing: false } activity) return;
            var body = Body(activity);
            var progress = new LinearProgressIndicator(activity) { Indeterminate = true };
            body.AddView(progress);
            _dialog = new MaterialAlertDialogBuilder(activity)
                .SetTitle(Translator.MacOS_AccountCreation_Title)
                .SetMessage(Translator.MacOS_AccountCreation_BrowserMessage)
                .SetView(body)
                .SetCancelable(false)
                .SetNegativeButton(Translator.Buttons_Cancel, (_, _) => cancellationTokenSource.Cancel())
                .Create();
            _dialog.Show();
        });

        public async void Complete(bool cancel)
        {
            try
            {
                await dispatcher.ExecuteOnUIThread(() =>
                {
                    if (_dialog is { IsShowing: true } dialog) dialog.Dismiss();
                    _dialog = null;
                });
            }
            catch (Exception exception) { Serilog.Log.Warning(exception, "Could not close the account progress dialog."); }
        }
    }

    #endregion

    #region Files

    /// <summary>The system document picker (any type; the Windows filters are extensions the picker cannot use).</summary>
    public async Task<List<SharedFile>> PickFilesAsync(params object[] typeFilters)
    {
        if (services.GetService<AndroidDocumentPicker>() is not { } picker) return [];
        try { return await picker.PickFilesAsync(); }
        catch (InvalidOperationException exception)
        {
            Serilog.Log.Warning(exception, "The document picker could not open.");
            return [];
        }
    }

    /// <summary>The content of the first picked document (theme wallpapers and similar single-file choices).</summary>
    public async Task<byte[]> PickWindowsFileContentAsync(params object[] typeFilters)
    {
        var files = await PickFilesAsync(typeFilters);
        return files.Count > 0 ? files[0].Data : [];
    }

    /// <summary>Picked documents as cached copies; the paths point to files the shared code can read.</summary>
    public async Task<List<PickedFileMetadata>> PickFilesMetadataAsync(params object[] typeFilters)
    {
        var files = await PickFilesAsync(typeFilters);
        return files.ConvertAll(static file => new PickedFileMetadata(file.FullFilePath, file.Data.LongLength));
    }

    #endregion

    #region Not built for Android yet

    private T NotYet<T>(T cancelled, string feature)
    {
        Serilog.Log.Information("{Feature} is not available on Android yet.", feature);
        ShowNotSupportedMessage();
        return cancelled;
    }

    public Task<string> PickWindowsFolderAsync() => Task.FromResult(NotYet(string.Empty, "Folder picking"));
    public Task<string> PickFilePathAsync(string saveFileName) => Task.FromResult(NotYet(string.Empty, "Saving files"));

    #endregion
}
