#if DEBUG
using Wino.Mail.Linux.Views.Contacts;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>Detail and menu verbs: detail, menu, menu-run path, menu-show, delete-selected, back.</summary>
internal static partial class ContactsDebugActions
{
    static partial void AddDetailVerbs()
    {
        Verb("detail", _ => Page.DescribeDetail());
        Verb("menu", async _ =>
        {
            var page = Page;
            var contact = page.ViewModel.SelectedContact ?? throw new ArgumentException("nothing selected");
            var entries = await page.BuildContextMenuAsync(contact);
            return $"targets={page.ViewModel.ResolveContactContextTargets(contact).Count} " + ContactContextMenu.Describe(entries);
        });
        Verb("menu-run", async args =>
        {
            var page = Page;
            var contact = page.ViewModel.SelectedContact ?? throw new ArgumentException("nothing selected");
            var entries = await page.BuildContextMenuAsync(contact);
            var entry = ContactContextMenu.Find(entries, string.Join('/', args)) ?? throw new ArgumentException("no entry");
            if (!entry.IsEnabled || entry.Run is null) return "disabled";
            entry.Run();
            return "ran " + entry.Title;
        });
        Verb("menu-show", async _ =>
        {
            var page = Page;
            var contact = page.ViewModel.SelectedContact ?? throw new ArgumentException("nothing selected");
            await page.OpenContextMenuAsync(contact, 120, 60);
            await Task.Delay(300);
            return $"open={ContextMenu.OpenMenus} live={WinoMenus.LiveMenus}";
        });
        Verb("menu-close", _ =>
        {
            ContextMenu.Current?.Popdown();
            return $"open={ContextMenu.OpenMenus} live={WinoMenus.LiveMenus}";
        });
        Verb("delete-selected", _ =>
        {
            var page = Page;
            var command = page.ViewModel.DeleteSelectedContactsCommand;
            if (!command.CanExecute(null)) return "cannot";
            command.ExecuteAsync(null).ContinueWith(static task => Serilog.Log.Error(task.Exception, "Debug delete failed."), TaskContinuationOptions.OnlyOnFaulted);
            return "started";
        });
        Verb("back", _ =>
        {
            Page.ViewModel.ClearSelectedContactCommand.Execute(null);
            return "cleared";
        });
    }
}
#endif
