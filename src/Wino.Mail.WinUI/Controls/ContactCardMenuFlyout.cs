using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Helpers;
using Wino.Mail.Controls.ContextFlyout;
using Wino.Mail.Controls.Core.ContextFlyout;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.WinUI.Controls;

public partial class ContactCardMenuFlyout : WinoContextFlyout
{
    private int _showRequestVersion;
#if DEBUG
    private readonly INotificationBuilder _notificationBuilder =
        WinoApplication.Current.Services.GetRequiredService<INotificationBuilder>();
#endif

    public async Task ShowForAsync(
        FrameworkElement target,
        Point? position,
        ContactsPageViewModel viewModel,
        AccountContactViewModel contact)
    {
        var requestVersion = ++_showRequestVersion;
        var contacts = viewModel.ResolveContactContextTargets(contact);
        var assignableLists = await viewModel.GetAssignableListsAsync(contacts);

        if (requestVersion != _showRequestVersion)
            return;

        if (target.XamlRoot is null)
            return;

        BuildItems(viewModel, contact, contacts, assignableLists);

        if (position is Point targetPosition)
        {
            ShowAt(target, WinoContextFlyoutHelper.CreatePointerAlignedOptions(targetPosition));
        }
        else
        {
            ShowAt(target, new FlyoutShowOptions
            {
                Placement = FlyoutPlacementMode.RightEdgeAlignedTop
            });
        }
    }

    private void BuildItems(
        ContactsPageViewModel viewModel,
        AccountContactViewModel contact,
        IReadOnlyList<AccountContactViewModel> contacts,
        IReadOnlyList<ContactList> assignableLists)
    {
        var items = new List<ContextFlyoutMenuEntry>();

        if (contacts.Count == 1 && contact.CanEdit)
        {
            items.Add(CreateCommandItem(
                Translator.ContactAction_Edit,
                WinoIconGlyph.Rename,
                "ContactCardContextEdit",
                viewModel.EditContactCommand,
                contact));
        }

        items.Add(CreateCommandItem(
            contacts.Any(item => !item.IsFavorite) ? Translator.ContactAction_Favorite : Translator.ContactAction_Unfavorite,
            WinoIconGlyph.Star,
            "ContactCardContextFavorite",
            new AsyncRelayCommand(() => viewModel.FavoriteContactsAsync(contacts)),
            null));

        if (contacts.Any(item => item.CanSendMail))
        {
            items.Add(CreateCommandItem(
                Translator.ContactAction_SendMail,
                WinoIconGlyph.Send,
                "ContactCardContextSendMail",
                new RelayCommand(() => viewModel.ComposeToContacts(contacts)),
                null));
        }

        if (assignableLists.Count > 0)
        {
            var assignItems = new List<ContextFlyoutMenuEntry>();
            foreach (var list in assignableLists)
            {
                assignItems.Add(new ContextFlyoutCommandEntry
                {
                    Text = list.Name,
                    Command = new AsyncRelayCommand(() => viewModel.AssignContactsToListAsync(list, contacts.Select(item => item.Id))),
                    AutomationId = $"ContactCardContextAssignList_{list.Id:N}"
                });
            }

            items.Add(new ContextFlyoutSubMenuEntry
            {
                Text = Translator.ContactAction_AddToList,
                Icon = CreateIcon(WinoIconGlyph.People),
                Items = assignItems,
                AutomationId = "ContactCardContextAssignToList"
            });
        }

#if DEBUG
        items.Add(ContextFlyoutSeparatorEntry.Instance);
        items.Add(new ContextFlyoutCommandEntry
        {
            Text = Translator.Buttons_TestNotification,
            Icon = CreateIcon(WinoIconGlyph.Reminder),
            Command = new AsyncRelayCommand(() => _notificationBuilder.CreateTestPeopleNotificationAsync(contact.SourceContact)),
            AutomationId = "ContactCardContextTestNotification"
        });
#endif

        if (contacts.Any(item => item.CanDelete))
        {
            items.Add(ContextFlyoutSeparatorEntry.Instance);
            items.Add(CreateCommandItem(
                Translator.ContactAction_Delete,
                WinoIconGlyph.Delete,
                "ContactCardContextDelete",
                new AsyncRelayCommand(() => viewModel.DeleteContactsAsync(contacts)),
                null,
                isDestructive: true,
                shortcut: new ContextFlyoutShortcut("Delete", "Delete")));
        }

        ItemsSource = items;
    }

    private static ContextFlyoutCommandEntry CreateCommandItem(
        string text,
        WinoIconGlyph icon,
        string automationId,
        System.Windows.Input.ICommand command,
        object commandParameter,
        bool isDestructive = false,
        ContextFlyoutShortcut? shortcut = null)
        => new()
        {
            Text = text,
            Icon = CreateIcon(icon),
            Command = command,
            CommandParameter = commandParameter,
            IsDestructive = isDestructive,
            Shortcut = shortcut,
            AutomationId = automationId
        };

    private static ContextFlyoutIcon? CreateIcon(WinoIconGlyph icon)
        => icon == WinoIconGlyph.None
            ? null
            : new ContextFlyoutIcon(WinoIconGlyphs.GetGlyph(icon));
}
