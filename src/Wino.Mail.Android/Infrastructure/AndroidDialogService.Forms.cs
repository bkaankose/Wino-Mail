using System.Collections.ObjectModel;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Mail.Android.Views.Dialogs;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using Wino.Core.Domain.Messaging.Server;
using Wino.Core.Domain.Messaging.UI;

namespace Wino.Mail.Android.Infrastructure;

/// <summary>
/// The form dialogs (the iOS UIKitDialogService.AliasCategory, Folders, Signature and AccountReorder partials; the Windows
/// ContentDialogs): Material dialogs built with <see cref="AndroidFormDialog{T}"/>, Cancel and a consequence-led primary
/// button, the Windows field order and an inline error.
/// </summary>
public sealed partial class AndroidDialogService
{
    /// <summary>A form dialog in the presentation gate; <paramref name="cancelled"/> when it is dismissed.</summary>
    private Task<T> FormAsync<T>(Func<AppCompatActivity, AndroidFormDialog<T>> build, T cancelled)
        => PresentAsync<T>((activity, completion) => build(activity).Build(completion), cancelled);

    /// <summary>
    /// Windows CreateAccountAliasDialog: alias address, then reply-to address. Always returns a dialog result whose alias is
    /// null when the person cancels, which is what the alias page checks.
    /// </summary>
    public async Task<ICreateAccountAliasDialog> ShowCreateAccountAliasDialogAsync()
    {
        var alias = await FormAsync<MailAccountAlias?>(activity =>
        {
            var form = new AndroidFormDialog<MailAccountAlias?>(activity, Translator.CreateAccountAliasDialog_Title, Translator.CreateAccountAliasDialog_Description, Translator.Buttons_Create);
            var address = form.AddTextField(Translator.CreateAccountAliasDialog_AliasAddress, email: true, helper: Translator.CreateAccountAliasDialog_AliasAddressPlaceholder);
            var replyTo = form.AddTextField(Translator.CreateAccountAliasDialog_ReplyToAddress, email: true, helper: Translator.CreateAccountAliasDialog_ReplyToAddressPlaceholder);
            form.CanConfirm = () => !string.IsNullOrWhiteSpace(address.Text);
            form.Primary = () =>
            {
                form.Finish(new MailAccountAlias
                {
                    AliasAddress = (address.Text ?? string.Empty).Trim(),
                    ReplyToAddress = (replyTo.Text ?? string.Empty).Trim(),
                    Id = Guid.NewGuid(),
                    IsPrimary = false,
                    IsVerified = false,
                    Source = AliasSource.Manual,
                    SendCapability = AliasSendCapability.Unknown
                });
                return Task.CompletedTask;
            };
            return form;
        }, null);
        return new AliasDialogResult { CreatedAccountAlias = alias };
    }

    /// <summary>Windows EditMailCategoryDialog: name, then one of the shared palette colours with a live chip; null when cancelled.</summary>
    public Task<MailCategoryDialogResult?> ShowEditMailCategoryDialogAsync(MailCategory? category = null) => FormAsync<MailCategoryDialogResult?>(activity =>
    {
        var palette = MailCategoryPalette.DefaultOptions;
        var selected = palette.FirstOrDefault(option =>
            string.Equals(option.BackgroundColorHex, category?.BackgroundColorHex, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(option.TextColorHex, category?.TextColorHex, StringComparison.OrdinalIgnoreCase)) ?? palette[0];

        var form = new AndroidFormDialog<MailCategoryDialogResult?>(activity,
            category is null ? Translator.MailCategoryDialog_CreateTitle : Translator.MailCategoryDialog_EditTitle, null, Translator.Buttons_Save);
        var name = form.AddTextField(Translator.MailCategoryDialog_Name, category?.Name, helper: Translator.MailCategoryDialog_NamePlaceholder);
        form.AddText(Translator.MailCategoryDialog_Color);
        var swatches = new SwatchGrid(activity, palette.Select(option => option.BackgroundColorHex).ToList()) { SelectedHex = selected.BackgroundColorHex, ContentDescription = Translator.MailCategoryDialog_Color };
        form.Add(swatches, Resource.Dimension.wino_space_2xs);

        // A live chip shows the name in the chosen pair, since the text colour is part of the choice.
        var chip = WinoTokens.Style(new TextView(activity), WinoTextStyle.BodyStrong);
        chip.SetPadding(WinoTokens.Px(activity, Resource.Dimension.wino_space_m), WinoTokens.Px(activity, Resource.Dimension.wino_space_2xs), WinoTokens.Px(activity, Resource.Dimension.wino_space_m), WinoTokens.Px(activity, Resource.Dimension.wino_space_2xs));
        var chipBackground = new GradientDrawable();
        chipBackground.SetCornerRadius(WinoTokens.PxF(activity, Resource.Dimension.wino_radius_small));
        chip.Background = chipBackground;
        var chipRow = new LinearLayout(activity);
        chipRow.AddView(chip);
        form.Add(chipRow);

        void UpdatePreview()
        {
            var fill = WinoColors.FromHexString(selected.BackgroundColorHex) ?? WinoColors.SubtleFill(activity);
            var text = WinoColors.FromHexString(selected.TextColorHex) ?? WinoColors.PrimaryText(activity);
            chipBackground.SetColor(fill);
            chipBackground.SetStroke(WinoTokens.Px(activity, Resource.Dimension.wino_stroke_hairline), text);
            chip.SetTextColor(text);
            chip.Text = string.IsNullOrWhiteSpace(name.Text) ? Translator.MailCategoryDialog_NamePlaceholder : name.Text.Trim();
        }

        swatches.Picked += (_, hex) =>
        {
            selected = palette.FirstOrDefault(option => option.BackgroundColorHex == hex) ?? selected;
            UpdatePreview();
        };
        name.TextChanged += (_, _) => UpdatePreview();
        form.CanConfirm = () => !string.IsNullOrWhiteSpace(name.Text);
        form.Primary = () =>
        {
            form.Finish(new MailCategoryDialogResult((name.Text ?? string.Empty).Trim(), selected.BackgroundColorHex, selected.TextColorHex));
            return Task.CompletedTask;
        };
        UpdatePreview();
        return form;
    }, null);

    /// <summary>Windows RenameFolderDialog: the server folder name and the local-only display name; null when cancelled.</summary>
    public Task<FolderRenameDialogResult?> ShowRenameFolderDialogAsync(string folderName, string? displayName, bool canRenameOnServer) => FormAsync<FolderRenameDialogResult?>(activity =>
    {
        var form = new AndroidFormDialog<FolderRenameDialogResult?>(activity, Translator.DialogMessage_RenameFolderTitle, null, Translator.FolderOperation_Rename);
        var name = form.AddTextField(Translator.RenameFolderDialog_FolderNameHeader, folderName, helper: canRenameOnServer ? Translator.RenameFolderDialog_FolderNameDescription : Translator.RenameFolderDialog_SystemFolderNameDescription);
        name.Enabled = canRenameOnServer;
        var display = form.AddTextField(Translator.RenameFolderDialog_DisplayNameHeader, displayName, helper: Translator.RenameFolderDialog_DisplayNameDescription);
        form.CanConfirm = () => !string.IsNullOrWhiteSpace(name.Text);
        form.Primary = () =>
        {
            form.Finish(new FolderRenameDialogResult((name.Text ?? string.Empty).Trim(), (display.Text ?? string.Empty).Trim()));
            return Task.CompletedTask;
        };
        return form;
    }, null);

    /// <summary>
    /// Windows SystemFolderConfigurationDialog plus the DialogService post-flow: save the configuration, show the success
    /// message, announce the folder change and request a full folder synchronization.
    /// </summary>
    public async Task HandleSystemFolderConfigurationDialogAsync(Guid accountId, IFolderService folderService)
    {
        try
        {
            var folders = await folderService.GetFoldersAsync(accountId) ?? [];
            var configuration = await FormAsync<SystemFolderConfiguration?>(activity => SystemFolderForm(activity, folders), null);
            if (configuration is null) return;

            await folderService.UpdateSystemFolderConfigurationAsync(accountId, configuration);
            InfoBarMessage(Translator.SystemFolderConfigSetupSuccess_Title, Translator.SystemFolderConfigSetupSuccess_Message, InfoBarMessageType.Success);
            WeakReferenceMessenger.Default.Send(new AccountFolderConfigurationUpdated(accountId));
            WeakReferenceMessenger.Default.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
            {
                AccountId = accountId,
                Type = MailSynchronizationType.FullFolders,
            }));
        }
        catch (Exception exception)
        {
            InfoBarMessage(Translator.Error_FailedToSetupSystemFolders_Title, exception.Message, InfoBarMessageType.Error);
        }
    }

    /// <summary>One dropdown per special folder (Sent, Draft, Archive, Trash, Junk), prefilled; the Windows validation.</summary>
    private static AndroidFormDialog<SystemFolderConfiguration?> SystemFolderForm(Context context, List<MailItemFolder> folders)
    {
        var description = $"{Translator.SystemFolderConfigDialog_MessageFirstLine}\n{Translator.SystemFolderConfigDialog_MessageSecondLine}";
        var form = new AndroidFormDialog<SystemFolderConfiguration?>(context, Translator.SettingsConfigureSpecialFolders_Title, description, Translator.Buttons_SaveConfiguration);
        // The first entry is "no folder", like the empty Windows ComboBox.
        var names = new List<string> { "—" };
        names.AddRange(folders.Select(folder => folder.ResolvedDisplayName ?? string.Empty));

        FormPopUp Picker(string header, SpecialFolderType type)
            => form.AddPopUp(header, names, folders.FindIndex(folder => folder.SpecialFolderType == type) + 1);

        var sent = Picker(Translator.SystemFolderConfigDialog_SentFolderHeader, SpecialFolderType.Sent);
        var draft = Picker(Translator.SystemFolderConfigDialog_DraftFolderHeader, SpecialFolderType.Draft);
        var archive = Picker(Translator.SystemFolderConfigDialog_ArchiveFolderHeader, SpecialFolderType.Archive);
        var trash = Picker(Translator.SystemFolderConfigDialog_DeletedFolderHeader, SpecialFolderType.Deleted);
        var junk = Picker(Translator.SystemFolderConfigDialog_JunkFolderHeader, SpecialFolderType.Junk);

        MailItemFolder? Selected(FormPopUp popup)
        {
            var index = popup.SelectedIndex - 1;
            return index >= 0 && index < folders.Count ? folders[index] : null;
        }

        form.Primary = () =>
        {
            // Same order and rules as Windows: Sent, Draft, Archive, Trash, Junk.
            var chosen = new[] { Selected(sent), Selected(draft), Selected(archive), Selected(trash), Selected(junk) }.OfType<MailItemFolder>().ToList();
            if (chosen.Select(folder => folder.Id).Distinct().Count() != chosen.Count) form.ShowError(Translator.SystemFolderConfigDialogValidation_DuplicateSystemFolders);
            else if (chosen.Any(folder => folder.SpecialFolderType == SpecialFolderType.Inbox)) form.ShowError(Translator.SystemFolderConfigDialogValidation_InboxSelected);
            // An unset slot is passed as null, as the Windows dialog does; the folder service skips it.
            else form.Finish(new SystemFolderConfiguration(Selected(sent), Selected(draft), Selected(archive), Selected(trash), Selected(junk)));
            return Task.CompletedTask;
        };
        return form;
    }

    /// <summary>
    /// Windows SignatureEditorDialog: the name, then the rich text editor. Same contract as Windows, the Mac and iOS: null
    /// when cancelled; a new signature gets a new Id and no account id, an edited one keeps its Id and MailAccountId.
    /// </summary>
    public async Task<AccountSignature?> ShowSignatureEditorDialog(AccountSignature? signatureModel = null)
    {
        var accountLine = await AccountLineAsync(signatureModel?.MailAccountId);
        var saved = await FormAsync<(string Name, string Html)?>(activity =>
        {
            var form = new AndroidFormDialog<(string Name, string Html)?>(activity,
                signatureModel is null ? Translator.SignatureEditorDialog_Title : signatureModel.Name ?? Translator.SignatureEditorDialog_Title,
                accountLine, Translator.Buttons_Save);
            var name = form.AddTextField(Translator.SignatureEditorDialog_SignatureName_TitleNew, signatureModel?.Name);
            var editor = form.Add(new WinoHtmlTextEditor(activity, Translator.SignatureEditorDialog_Title, Resource.Dimension.wino_editor_compact_min_height));
            editor.Html = signatureModel?.HtmlBody ?? string.Empty;
            form.CanConfirm = () => !string.IsNullOrWhiteSpace(name.Text);
            form.Primary = () =>
            {
                form.Finish(((name.Text ?? string.Empty).Trim(), editor.Html));
                return Task.CompletedTask;
            };
            return form;
        }, null);
        if (saved is not { } result) return null;
        if (signatureModel is null)
            return new AccountSignature { Id = Guid.NewGuid(), Name = result.Name, HtmlBody = result.Html };
        return new AccountSignature { Id = signatureModel.Id, Name = result.Name, MailAccountId = signatureModel.MailAccountId, HtmlBody = result.Html };
    }

    /// <summary>"Account name · address" under the signature title, when the signature's account is known.</summary>
    private async Task<string?> AccountLineAsync(Guid? accountId)
    {
        if (accountId is not { } id || id == Guid.Empty || services.GetService<IAccountService>() is not { } accounts) return null;
        try
        {
            var account = await accounts.GetAccountAsync(id);
            if (account is null) return null;
            return string.IsNullOrWhiteSpace(account.DisplayAddress) || account.DisplayAddress == account.Name ? account.Name : $"{account.Name} · {account.DisplayAddress}";
        }
        catch (Exception exception)
        {
            Serilog.Log.Warning(exception, "Could not read the signature's account.");
            return null;
        }
    }

    /// <summary>
    /// Windows AccountReorderDialog: the account layout with Move up and Move down per row (TalkBack reachable; Android has
    /// no always-on reorder handles). Top-level accounts and groups move among themselves; a group's accounts follow it,
    /// indented, and move only inside the group (membership changes on the group's page). The dialog reads the layout
    /// itself, because the flat list the settings page passes does not carry the groups. Save stores the layout order;
    /// Cancel changes nothing.
    /// </summary>
    public async Task ShowAccountReorderDialogAsync(ObservableCollection<IAccountProviderDetailViewModel> availableAccounts)
    {
        if (services.GetService<IAccountService>() is not { } accountService) return;
        ObservableCollection<AccountReorderItem> items;
        try
        {
            var accounts = await accountService.GetAccountsAsync();
            var groups = await accountService.GetAccountGroupsAsync();
            items = AccountReorderItem.FromLayout(AccountLayoutBuilder.Build(accounts, groups));
        }
        catch (Exception exception)
        {
            Serilog.Log.Warning(exception, "Could not read the account layout.");
            InfoBarMessage(Translator.GeneralTitle_Error, exception.Message, InfoBarMessageType.Error);
            return;
        }

        await FormAsync<bool?>(activity =>
        {
            var form = new AndroidFormDialog<bool?>(activity, Translator.SettingsReorderAccounts_Title,
                Translator.SettingsReorderAccounts_Description, Translator.Buttons_Save);
            var list = form.Add(new LinearLayout(activity) { Orientation = Orientation.Vertical });
            void Render()
            {
                list.RemoveAllViews();
                for (int index = 0; index < items.Count; index++)
                {
                    var item = items[index];
                    list.AddView(ReorderRow(activity, item, items, index, indented: false, Render));
                    for (int child = 0; child < item.Children.Count; child++)
                        list.AddView(ReorderRow(activity, item.Children[child], item.Children, child, indented: true, Render));
                }
            }
            Render();
            form.Primary = async () =>
            {
                form.SetBusy(true);
                try
                {
                    await accountService.SaveAccountLayoutOrderAsync(AccountReorderItem.ToOrder(items));
                    form.Finish(true);
                }
                catch (Exception exception)
                {
                    form.SetBusy(false);
                    form.ShowError(exception.Message);
                }
            };
            return form;
        }, null);
    }

    /// <summary>
    /// One row of the reorder dialog: a group shows its icon and name in its colour, an account its name; both with the
    /// supporting line and Move up / Move down inside <paramref name="siblings"/>.
    /// </summary>
    private static View ReorderRow(Context context, AccountReorderItem item, ObservableCollection<AccountReorderItem> siblings, int position, bool indented, Action changed)
    {
        var row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetMinimumHeight(WinoTokens.Px(context, Resource.Dimension.wino_size_touch_large));
        // Members line up with their group's name, after the group icon.
        if (indented) row.SetPaddingRelative(WinoTokens.Px(context, Resource.Dimension.wino_size_swatch_large) + WinoTokens.Px(context, Resource.Dimension.wino_space_s), 0, 0, 0);

        if (item.IsGroup)
        {
            var tint = WinoColors.AccountGroupColor(context, item.ColorKey) ?? WinoColors.PrimaryText(context);
            var glyph = new WinoIconView(context) { Icon = item.IconGlyph, Colorful = false, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_l), Gravity = GravityFlags.Center };
            glyph.SetTextColor(tint);
            glyph.ImportantForAccessibility = ImportantForAccessibility.No;
            row.AddView(glyph, new LinearLayout.LayoutParams(WinoTokens.Px(context, Resource.Dimension.wino_size_swatch_large), WinoTokens.Px(context, Resource.Dimension.wino_size_swatch_large))
            {
                MarginEnd = WinoTokens.Px(context, Resource.Dimension.wino_space_s)
            });
        }

        var text = new LinearLayout(context) { Orientation = Orientation.Vertical };
        var title = WinoTokens.Style(new TextView(context) { Text = item.Title }, item.IsGroup ? WinoTextStyle.BodyStrong : WinoTextStyle.Body);
        if (item.IsGroup) title.SetTextColor(WinoColors.AccountGroupColor(context, item.ColorKey) ?? WinoColors.PrimaryText(context));
        title.SetMaxLines(1);
        title.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        text.AddView(title);
        if (!string.IsNullOrWhiteSpace(item.Subtitle))
        {
            var subtitle = WinoTokens.Style(new TextView(context) { Text = item.Subtitle }, WinoTextStyle.Caption);
            subtitle.SetTextColor(WinoColors.SecondaryText(context));
            subtitle.SetMaxLines(1);
            subtitle.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
            text.AddView(subtitle);
        }
        text.Focusable = true;
        text.ContentDescription = string.IsNullOrWhiteSpace(item.Subtitle) ? item.Title : $"{item.Title}, {item.Subtitle}";
        row.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));

        row.AddView(MoveButton(context, WinoIconGlyph.ChevronUp, $"{Translator.MailFilters_MoveUp}: {item.Title}", position > 0, () =>
        {
            siblings.Move(position, position - 1);
            changed();
        }));
        row.AddView(MoveButton(context, WinoIconGlyph.ChevronDown, $"{Translator.MailFilters_MoveDown}: {item.Title}", position < siblings.Count - 1, () =>
        {
            siblings.Move(position, position + 1);
            changed();
        }));
        return row;
    }

    private static View MoveButton(Context context, WinoIconGlyph glyph, string label, bool enabled, Action move)
    {
        var button = new WinoIconView(context) { Icon = glyph, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_m), ContentDescription = label, Clickable = enabled, Focusable = enabled, Gravity = GravityFlags.Center, Enabled = enabled };
        button.SetTextColor(enabled ? WinoColors.PrimaryText(context) : WinoColors.TertiaryText(context));
        button.LayoutParameters = new LinearLayout.LayoutParams(WinoTokens.Px(context, Resource.Dimension.wino_size_touch), WinoTokens.Px(context, Resource.Dimension.wino_size_touch));
        if (enabled) button.Click += (_, _) => move();
        return button;
    }

    /// <summary>
    /// Keyboard shortcuts need a hardware keyboard handler, which Android does not have yet
    /// (<see cref="IPlatformCapabilities.KeyboardShortcuts"/> is false, so the Keyboard shortcuts page is not listed and
    /// nothing opens this). It answers like Cancel.
    /// </summary>
    public Task<KeyboardShortcutDialogResult> ShowKeyboardShortcutDialogAsync(KeyboardShortcut? existingShortcut = null)
    {
        Serilog.Log.Information("Keyboard shortcuts are not offered on Android (no hardware shortcut handling).");
        return Task.FromResult(KeyboardShortcutDialogResult.Canceled());
    }

    /// <summary>The alias dialog contract; the alias stays null when cancelled.</summary>
    private sealed class AliasDialogResult : ICreateAccountAliasDialog
    {
        public MailAccountAlias? CreatedAccountAlias { get; set; }
    }

    /// <summary>A wrapping grid of round colour swatches (iOS WinoColorSwatchPicker); the selected one carries a ring.</summary>
    private sealed class SwatchGrid : GridLayout
    {
        private readonly List<(string Hex, View View, GradientDrawable Ring)> _items = [];

        public SwatchGrid(Context context, IReadOnlyList<string> colors) : base(context)
        {
            ColumnCount = 7;
            int size = WinoTokens.Px(context, Resource.Dimension.wino_size_swatch_large);
            int gap = WinoTokens.Px(context, Resource.Dimension.wino_space_2xs);
            foreach (var hex in colors)
            {
                var fill = new GradientDrawable();
                fill.SetShape(ShapeType.Oval);
                fill.SetColor(WinoColors.FromHexString(hex) ?? WinoTokens.Color(context, Resource.Color.wino_swatch_fallback));
                fill.SetStroke(WinoTokens.Px(context, Resource.Dimension.wino_stroke_hairline), WinoColors.CardStroke(context));
                var ring = new GradientDrawable();
                ring.SetShape(ShapeType.Oval);
                ring.SetColor(Color.Transparent);
                var swatch = new View(context) { Background = fill, Foreground = ring, Clickable = true, Focusable = true, ContentDescription = hex };
                swatch.Click += (_, _) =>
                {
                    SelectedHex = hex;
                    Picked?.Invoke(this, hex);
                };
                var layout = new LayoutParams { Width = size, Height = size };
                layout.SetMargins(gap, gap, gap, gap);
                AddView(swatch, layout);
                _items.Add((hex, swatch, ring));
            }
        }

        public event EventHandler<string>? Picked;

        public string? SelectedHex
        {
            set
            {
                var context = this.RequireContext();
                foreach (var (hex, view, ring) in _items)
                {
                    bool selected = string.Equals(hex, value, StringComparison.OrdinalIgnoreCase);
                    ring.SetStroke(selected ? WinoTokens.Px(context, Resource.Dimension.wino_stroke_selection) : 0, WinoColors.PrimaryText(context));
                    view.Selected = selected;
                }
            }
        }
    }
}
