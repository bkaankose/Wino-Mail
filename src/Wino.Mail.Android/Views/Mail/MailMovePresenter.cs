using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Dialog;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Android.Shell;
using Wino.Mail.ViewModels;
using Wino.Presentation.Android;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace Wino.Mail.Android.Views.Mail;

/// <summary>
/// "Move to folder" (canvas Material pass board MoveSheet; the iPhone MoveFolderSheet and Mac MoveFolderPopover): the
/// account's folders in navigation order as indented Material 3 drawer items under the account subheader, with a filter
/// field, in a modal bottom sheet on compact widths and a dialog on wider windows. The source folder and virtual groups are shown but not selectable
/// (<see cref="IMailItemFolder.IsMoveTarget"/>). A tap moves the mails through the list ViewModel's shared operation
/// path; mixed-account selections are left to the shared picker (the caller falls back when this returns false).
/// </summary>
internal static class MailMovePresenter
{
    /// <summary>Opens the picker for <paramref name="mails"/>; false when they span accounts or there are none.</summary>
    public static bool TryShow(Fragment host, MailListPageViewModel list, IReadOnlyList<MailCopy> mails, bool compact, Action? moved = null)
    {
        if (mails.Count == 0 || host.Context is null) return false;
        var accountIds = mails.Select(static mail => mail.AssignedAccount?.Id).Distinct().ToArray();
        if (accountIds.Length != 1 || accountIds[0] is not Guid accountId) return false;
        _ = ShowAsync(host, list, mails, accountId, compact, moved);
        return true;
    }

    private static async Task ShowAsync(Fragment host, MailListPageViewModel list, IReadOnlyList<MailCopy> mails, Guid accountId, bool compact, Action? moved)
    {
        try
        {
            if (MainApplication.Services?.GetService<IFolderService>() is not { } folderService) return;
            IReadOnlyList<IMailItemFolder> folders = await folderService.GetFolderStructureForDisplayAsync(accountId);
            var sources = mails.Select(static mail => mail.AssignedFolder?.Id).OfType<Guid>().ToHashSet();
            host.Activity?.RunOnUiThread(() =>
            {
                if (host.Context is not { } context || !host.IsAdded) return;
                Present(context, folders, sources, mails, compact, folder =>
                {
                    _ = list.ExecuteMailOperationAsync(new MailOperationPreperationRequest(MailOperation.Move, mails, moveTargetFolder: folder));
                    moved?.Invoke();
                });
            });
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "The Move picker could not open."); }
    }

    private static void Present(Context context, IReadOnlyList<IMailItemFolder> folders, IReadOnlySet<Guid> sources, IReadOnlyList<MailCopy> mails,
        bool compact, Action<IMailItemFolder> picked)
    {
        var all = new List<(IMailItemFolder Folder, int Depth)>();
        Flatten(folders, 0, all);
        bool Selectable(IMailItemFolder folder) => folder.IsMoveTarget && !sources.Contains(folder.Id);

        var column = LayoutInflater.From(context)?.Inflate(Resource.Layout.mail_move_sheet, null, false)
            ?? throw new InvalidOperationException("mail_move_sheet.xml did not inflate.");
        column.Require<View>(Resource.Id.move_handle).Visibility = compact ? ViewStates.Visible : ViewStates.Gone;
        column.Require<TextView>(Resource.Id.move_title).Text = Translator.MailOperation_Move;
        column.Require<TextView>(Resource.Id.move_subtitle).Text = mails.Count == 1 ? mails[0].Subject : string.Format(Translator.MailsSelected, mails.Count);
        var account = column.Require<TextView>(Resource.Id.move_account);
        account.Text = mails[0].AssignedAccount?.Address ?? string.Empty;
        account.SetTextColor(WinoColors.AccentText(context));
        account.Visibility = string.IsNullOrWhiteSpace(account.Text) ? ViewStates.Gone : ViewStates.Visible;
        var glyph = column.Require<WinoIconView>(Resource.Id.move_search_glyph);
        glyph.Icon = WinoIconGlyph.Find;
        glyph.GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph_small);
        var search = column.Require<EditText>(Resource.Id.move_search);
        search.Hint = Translator.ContextFlyout_SearchPlaceholder;

        Dialog? dialog = null;
        var adapter = new FolderAdapter(all, Selectable, folder =>
        {
            dialog?.Dismiss();
            picked(folder);
        });
        var listView = column.Require<RecyclerView>(Resource.Id.move_list);
        listView.SetLayoutManager(new LinearLayoutManager(context));
        listView.SetAdapter(adapter);
        if (listView.LayoutParameters is { } listParams)
        {
            int screen = context.Resources?.DisplayMetrics?.HeightPixels ?? 0;
            listParams.Height = Math.Min(WinoTokens.Px(context, Resource.Dimension.wino_m3_sheet_list_max_height),
                (int)(screen * WinoColors.Fraction(context, Resource.Fraction.wino_m3_sheet_list_screen_fraction)));
            listView.LayoutParameters = listParams;
        }
        search.TextChanged += (_, _) =>
        {
            var text = search.Text?.Trim() ?? string.Empty;
            adapter.SetRows(text.Length == 0
                ? all
                : all.Where(entry => Selectable(entry.Folder) && (entry.Folder.FolderName ?? string.Empty).Contains(text, StringComparison.CurrentCultureIgnoreCase))
                    .Select(static entry => (entry.Folder, 0)).ToList());
        };

        if (compact)
        {
            // Modal bottom sheet: the drag handle, back and the scrim dismiss it (Material 3 has no Cancel button here).
            var sheet = new BottomSheetDialog(context);
            sheet.SetContentView(column);
            sheet.Behavior.State = BottomSheetBehavior.StateExpanded;
            sheet.Behavior.SkipCollapsed = true;
            dialog = sheet;
            sheet.Show();
            return;
        }
        var alert = new MaterialAlertDialogBuilder(context)
            .SetView(column)!
            .SetNegativeButton(Translator.Buttons_Cancel, (_, _) => { })!
            .Create();
        dialog = alert;
        alert.Show();
    }

    private static void Flatten(IEnumerable<IMailItemFolder>? folders, int depth, List<(IMailItemFolder, int)> into)
    {
        foreach (var folder in folders ?? [])
        {
            if (folder is null) continue;
            into.Add((folder, depth));
            Flatten(folder.ChildFolders, depth + 1, into);
        }
    }

    private sealed class FolderAdapter(IReadOnlyList<(IMailItemFolder Folder, int Depth)> rows, Func<IMailItemFolder, bool> selectable, Action<IMailItemFolder> picked)
        : RecyclerView.Adapter
    {
        private IReadOnlyList<(IMailItemFolder Folder, int Depth)> _rows = rows;

        public void SetRows(IReadOnlyList<(IMailItemFolder Folder, int Depth)> next)
        {
            _rows = next;
            NotifyDataSetChanged();
        }

        public override int ItemCount => _rows.Count;

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        {
            var view = new WinoShellRowView(parent.RequireContext())
            {
                LayoutParameters = new RecyclerView.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            };
            view.Click += (_, _) =>
            {
                if (view.Item is IMailItemFolder folder && selectable(folder)) picked(folder);
            };
            return new Holder(view);
        }

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
        {
            if (holder is not Holder row) return;
            var (folder, depth) = _rows[position];
            row.View.Item = folder;
            row.View.ConfigureFolder(SpecialFolderGlyphs.GetGlyphOrFolder(folder.SpecialFolderType), folder.FolderName ?? string.Empty, 0, depth);
            bool enabled = selectable(folder);
            row.View.Alpha = enabled ? 1f : WinoColors.Fraction(row.View.RequireContext(), Resource.Fraction.wino_m3_disabled_alpha);
            row.View.Enabled = enabled;
        }

        private sealed class Holder(WinoShellRowView view) : RecyclerView.ViewHolder(view)
        {
            public WinoShellRowView View { get; } = view;
        }
    }
}
