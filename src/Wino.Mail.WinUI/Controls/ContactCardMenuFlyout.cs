using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.ContextFlyout;
using Wino.Mail.Controls.Core.ContextFlyout;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;

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
        var assignableLists = await viewModel.GetAssignableListsAsync(contact);

        if (requestVersion != _showRequestVersion)
            return;

        if (target.XamlRoot is null)
            return;

        BuildItems(viewModel, contact, assignableLists);

        if (position is Point targetPosition)
        {
            ShowAt(target, new FlyoutShowOptions
            {
                Position = targetPosition,
                Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft
            });
        }
        else
        {
            ShowAt(target, new FlyoutShowOptions
            {
                Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft
            });
        }
    }

    private void BuildItems(
        ContactsPageViewModel viewModel,
        AccountContactViewModel contact,
        IReadOnlyList<ContactList> assignableLists)
    {
        var items = new List<ContextFlyoutMenuEntry>();

        if (contact.CanEdit)
        {
            items.Add(CreateCommandItem(
                Translator.ContactAction_Edit,
                "\uE70F",
                "ContactCardContextEdit",
                viewModel.EditContactCommand,
                contact));
        }

        items.Add(CreateCommandItem(
            contact.FavoriteActionText,
            "\uE734",
            "ContactCardContextFavorite",
            viewModel.ToggleFavoriteCommand,
            contact));

        if (contact.CanSendMail)
        {
            items.Add(CreateCommandItem(
                Translator.ContactAction_SendMail,
                "\uE715",
                "ContactCardContextSendMail",
                viewModel.ComposeToContactCommand,
                contact));
        }

        if (assignableLists.Count > 0)
        {
            var assignItems = new List<ContextFlyoutMenuEntry>();
            foreach (var list in assignableLists)
            {
                assignItems.Add(new ContextFlyoutCommandEntry
                {
                    Text = list.Name,
                    Command = new AsyncRelayCommand(() => viewModel.AssignContactsToListAsync(list, (System.Guid[])[contact.Id])),
                    AutomationId = $"ContactCardContextAssignList_{list.Id:N}"
                });
            }

            items.Add(new ContextFlyoutSubMenuEntry
            {
                Text = Translator.ContactAction_AddToList,
                Icon = CreateIcon("\uE8FD"),
                Items = assignItems,
                AutomationId = "ContactCardContextAssignToList"
            });
        }

#if DEBUG
        items.Add(ContextFlyoutSeparatorEntry.Instance);
        items.Add(new ContextFlyoutCommandEntry
        {
            Text = Translator.Buttons_TestNotification,
            Icon = CreateIcon("\uE7ED"),
            Command = new AsyncRelayCommand(() => _notificationBuilder.CreateTestPeopleNotificationAsync(contact.SourceContact)),
            AutomationId = "ContactCardContextTestNotification"
        });
#endif

        if (contact.CanDelete)
        {
            items.Add(ContextFlyoutSeparatorEntry.Instance);
            items.Add(CreateCommandItem(
                Translator.ContactAction_Delete,
                "\uE74D",
                "ContactCardContextDelete",
                viewModel.DeleteContactCommand,
                contact,
                isDestructive: true,
                shortcut: new ContextFlyoutShortcut("Delete", "Delete")));
        }

        ItemsSource = items;
    }

    private static ContextFlyoutCommandEntry CreateCommandItem(
        string text,
        string glyph,
        string automationId,
        System.Windows.Input.ICommand command,
        object commandParameter,
        bool isDestructive = false,
        ContextFlyoutShortcut? shortcut = null)
        => new()
        {
            Text = text,
            Icon = CreateIcon(glyph),
            Command = command,
            CommandParameter = commandParameter,
            IsDestructive = isDestructive,
            Shortcut = shortcut,
            AutomationId = automationId
        };

    private static ContextFlyoutIcon CreateIcon(string glyph) => new(glyph);
}
