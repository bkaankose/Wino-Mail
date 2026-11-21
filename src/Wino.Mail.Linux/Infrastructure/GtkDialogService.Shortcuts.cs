using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Mail.Linux.Views.Dialogs;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class GtkDialogService
{
    public Task<KeyboardShortcutDialogResult> ShowKeyboardShortcutDialogAsync(KeyboardShortcut? existingShortcut = null)
        => PresentAsync(owner => new KeyboardShortcutDialog(existingShortcut, services.GetService<IKeyboardShortcutService>(),
                owner.GetApplication() ?? Gio.Application.GetDefault() as Gtk.Application)
            .PresentAsync(owner), KeyboardShortcutDialogResult.Canceled());
}
