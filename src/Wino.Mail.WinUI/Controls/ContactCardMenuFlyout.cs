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
using Wino.Helpers;
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
        IReadOnlyList<ContactList> assignableLists)
    {
        var items = new List<ContextFlyoutMenuEntry>();

        if (contact.CanEdit)
        {
            items.Add(CreateCommandItem(
                Translator.ContactAction_Edit,
                WinoIconGlyph.Rename,
                "ContactCardContextEdit",
                viewModel.EditContactCommand,
                contact));
        }

        items.Add(CreateCommandItem(
            contact.FavoriteActionText,
            WinoIconGlyph.Star,
            "ContactCardContextFavorite",
            viewModel.ToggleFavoriteCommand,
            contact));

        if (contact.CanSendMail)
        {
            items.Add(CreateCommandItem(
                Translator.ContactAction_SendMail,
                WinoIconGlyph.Send,
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

        if (contact.CanDelete)
        {
            items.Add(ContextFlyoutSeparatorEntry.Instance);
            items.Add(CreateCommandItem(
                Translator.ContactAction_Delete,
                WinoIconGlyph.Delete,
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
        => ControlConstants.WinoIconFontDictionary.TryGetValue(icon, out var glyph)
            ? new ContextFlyoutIcon(glyph)
            : null;
}
