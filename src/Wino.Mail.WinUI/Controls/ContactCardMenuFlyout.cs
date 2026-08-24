using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.WinUI.Controls;

public partial class ContactCardMenuFlyout : WinoMenuFlyout
{
    private int _showRequestVersion;

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
        Items.Clear();

        if (contact.CanEdit)
        {
            Items.Add(CreateCommandItem(
                Translator.ContactAction_Edit,
                "\uE70F",
                "ContactCardContextEdit",
                viewModel.EditContactCommand,
                contact));
        }

        Items.Add(CreateCommandItem(
            contact.FavoriteActionText,
            "\uE734",
            "ContactCardContextFavorite",
            viewModel.ToggleFavoriteCommand,
            contact));

        if (contact.CanSendMail)
        {
            Items.Add(CreateCommandItem(
                Translator.ContactAction_SendMail,
                "\uE715",
                "ContactCardContextSendMail",
                viewModel.ComposeToContactCommand,
                contact));
        }

        if (assignableLists.Count > 0)
        {
            var assignSubItem = new MenuFlyoutSubItem
            {
                Text = Translator.ContactAction_AddToList,
                Icon = CreateIcon("\uE8FD")
            };
            AutomationProperties.SetAutomationId(assignSubItem, "ContactCardContextAssignToList");

            foreach (var list in assignableLists)
            {
                var listItem = new MenuFlyoutItem
                {
                    Text = list.Name,
                    Tag = list
                };
                AutomationProperties.SetAutomationId(listItem, $"ContactCardContextAssignList_{list.Id:N}");
                listItem.Click += async (_, _) => await viewModel.AssignContactsToListAsync(list, new[] { contact.Id });
                assignSubItem.Items.Add(listItem);
            }

            Items.Add(assignSubItem);
        }

        if (contact.CanDelete)
        {
            Items.Add(new MenuFlyoutSeparator());
            Items.Add(CreateCommandItem(
                Translator.ContactAction_Delete,
                "\uE74D",
                "ContactCardContextDelete",
                viewModel.DeleteContactCommand,
                contact));
        }
    }

    private static MenuFlyoutItem CreateCommandItem(
        string text,
        string glyph,
        string automationId,
        System.Windows.Input.ICommand command,
        object commandParameter)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            Icon = CreateIcon(glyph),
            Command = command,
            CommandParameter = commandParameter
        };
        AutomationProperties.SetAutomationId(item, automationId);
        return item;
    }

    private static FontIcon CreateIcon(string glyph)
        => new() { Glyph = glyph, FontSize = 16 };
}
