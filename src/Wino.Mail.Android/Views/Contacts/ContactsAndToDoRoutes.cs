using Wino.Core.Domain.Enums;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Android.Views.ToDo;
using Wino.Mail.ViewModels;

namespace Wino.Mail.Android.Views.Contacts;

/// <summary>
/// The Contacts and To Do routes (Windows ContactsPage, ContactEditPage and ToDoPage). The lists are the modes' shell
/// content. The contact editor is a window-host page: with the shell up the shared coordinator shows it as the secondary
/// page over everything (a Material 3 full-screen dialog on every width), and its Save and Cancel leave through the
/// coordinator's back, which closes that page without touching the mode's content history. The contact card and the task
/// detail have no Windows page; the list pages place them through <see cref="AndroidNavigationService.ShowDetailAsync"/>.
/// </summary>
public static class ContactsAndToDoRoutes
{
    public static AndroidPageRegistry RegisterContactsAndToDoPages(this AndroidPageRegistry registry)
    {
        registry.Register<ContactsFragment, ContactsPageViewModel>(WinoPage.ContactsPage, AndroidPageHost.ShellContent);
        registry.Register<ContactEditorFragment, ContactEditPageViewModel>(WinoPage.ContactEditPage, AndroidPageHost.Root,
            initialize: static (viewModel, mode, parameter) => viewModel.InitializeNavigationAsync(mode, parameter));
        registry.Register<ToDoFragment, ToDoPageViewModel>(WinoPage.ToDoPage, AndroidPageHost.ShellContent);
        return registry;
    }
}
