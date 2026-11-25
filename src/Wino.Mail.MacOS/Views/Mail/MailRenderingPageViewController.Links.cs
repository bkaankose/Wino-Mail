using AppKit;
using Wino.Core.Domain;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.MacOS.Views.Links;
using Wino.Mail.ViewModels;
using Wino.Presentation.AppKit;
using Wino.Presentation.Bindings;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>Tasks and events created from the open message (Windows MailRenderingPage linked items panel).</summary>
public sealed partial class MailRenderingPageViewController
{
    private NSStackView _linkedRow = null!;
    private LinkedItemButtons _linkedItems = null!;

    private NSStackView BuildLinkedItemsRow()
    {
        _linkedItems = new LinkedItemButtons(item => Observe(ViewModel.OpenLinkedItemCommand.ExecuteAsync(item)), NSUserInterfaceLayoutOrientation.Horizontal);
        var label = WinoStyle.Label(Translator.ConnectedItems_LinkedHeader, WinoStyle.Caption, WinoStyle.SecondaryText);
        label.SetContentCompressionResistancePriority(750, NSLayoutConstraintOrientation.Horizontal);
        _linkedRow = WinoLayout.HStack(6, label, _linkedItems);
        _linkedRow.Alignment = NSLayoutAttribute.CenterY;
        _linkedRow.Hidden = true;
        _linkedItems.Attach(ViewModel.LinkedItems);
        Bindings.Own(new ActionDisposable(_linkedItems.Detach));
        Bind(nameof(ViewModel.HasLinkedItems), static vm => vm.HasLinkedItems, visible => _linkedRow.Hidden = !visible);
        return _linkedRow;
    }
}
