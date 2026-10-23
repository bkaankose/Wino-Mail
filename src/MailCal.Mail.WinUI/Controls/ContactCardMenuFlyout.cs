using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Interfaces;
using MailCal.Helpers;
using MailCal.Mail.Controls.ContextFlyout;
using MailCal.Mail.Controls.Core.ContextFlyout;
using MailCal.Mail.ViewModels;
using MailCal.Mail.ViewModels.Data;
using MailCal.Core.Domain.Enums;

namespace MailCal.Mail.WinUI.Controls;

public partial class ContactCardMenuFlyout : MailCalContextFlyout
{
    private int _showRequestVersion;
#if DEBUG
    private readonly INotificationBuilder _notificationBuilder =
        MailCalApplication.Current.Services.GetRequiredService<INotificationBuilder>();
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

        BuildItems(viewModel, contact, contacts, assignableLists, viewModel.GetAvailableCategories(contacts));

        if (position is Point targetPosition)
        {
            ShowAt(target, MailCalContextFlyoutHelper.CreatePointerAlignedOptions(targetPosition));
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
        IReadOnlyList<ContactList> assignableLists,
        IReadOnlyList<MailCategory> categories)
    {
        var items = new List<ContextFlyoutMenuEntry>();

        if (contacts.Count == 1 && contact.CanEdit)
        {
            items.Add(CreateCommandItem(
                Translator.ContactAction_Edit,
                MailCalIconGlyph.Rename,
                "ContactCardContextEdit",
                viewModel.EditContactCommand,
                contact));
        }

        items.Add(CreateCommandItem(
            contacts.Any(item => !item.IsFavorite) ? Translator.ContactAction_Favorite : Translator.ContactAction_Unfavorite,
            MailCalIconGlyph.Star,
            "ContactCardContextFavorite",
            new AsyncRelayCommand(() => viewModel.FavoriteContactsAsync(contacts)),
            null));

        if (contacts.Any(item => item.CanSendMail))
        {
            items.Add(CreateCommandItem(
                Translator.ContactAction_SendMail,
                MailCalIconGlyph.Send,
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
                Icon = CreateIcon(MailCalIconGlyph.People),
                Items = assignItems,
                AutomationId = "ContactCardContextAssignToList"
            });
        }

        if (categories.Count > 0)
        {
            var categoryItems = new List<ContextFlyoutMenuEntry>();
            foreach (var category in categories)
            {
                categoryItems.Add(new ContextFlyoutToggleEntry
                {
                    Text = category.Name,
                    SearchKeywords = category.Name,
                    Icon = new ContextFlyoutIcon(MailCalIconGlyphs.GetGlyph(MailCalIconGlyph.SpecialFolderCategory), category.TextColorHex),
                    IsChecked = contacts.All(item => item.Categories.Any(assigned => assigned.Id == category.Id)),
                    Command = new AsyncRelayCommand(() => viewModel.ToggleContactCategoryAsync(category, contacts)),
                    AutomationId = $"ContactCardContextCategory_{category.Id:N}"
                });
            }

            items.Add(new ContextFlyoutSubMenuEntry
            {
                Text = Translator.MailCategoryMenuItem,
                Icon = CreateIcon(MailCalIconGlyph.SpecialFolderCategory),
                Items = categoryItems,
                AutomationId = "ContactCardContextCategories"
            });
        }

#if DEBUG
        items.Add(ContextFlyoutSeparatorEntry.Instance);
        items.Add(new ContextFlyoutCommandEntry
        {
            Text = Translator.Buttons_TestNotification,
            Icon = CreateIcon(MailCalIconGlyph.Reminder),
            Command = new AsyncRelayCommand(() => _notificationBuilder.CreateTestPeopleNotificationAsync(contact.SourceContact)),
            AutomationId = "ContactCardContextTestNotification"
        });
#endif

        if (contacts.Any(item => item.CanDelete))
        {
            items.Add(ContextFlyoutSeparatorEntry.Instance);
            items.Add(CreateCommandItem(
                Translator.ContactAction_Delete,
                MailCalIconGlyph.Delete,
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
        MailCalIconGlyph icon,
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

    private static ContextFlyoutIcon? CreateIcon(MailCalIconGlyph icon)
        => icon == MailCalIconGlyph.None
            ? null
            : new ContextFlyoutIcon(MailCalIconGlyphs.GetGlyph(icon));
}
