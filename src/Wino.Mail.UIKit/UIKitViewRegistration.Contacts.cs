using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.UIKit.Views.Contacts;

namespace Wino.Mail.UIKit;

/// <summary>
/// Contacts pages shared by both idioms (Mac MacViewModelRegistration.Contacts). The list page itself is an idiom subclass
/// registered by PhoneViewRegistration/PadViewRegistration; the editor is shared. Debug builds add the seed commands.
/// </summary>
public static partial class UIKitViewRegistration
{
    static partial void RegisterContactsViews(IServiceCollection services);

    static partial void RegisterContactsViews(IServiceCollection services)
    {
        services.AddTransient<ContactEditPageViewController>();
#if DEBUG
        services.AddSingleton<Wino.Mail.ViewModels.Lifecycle.IApplicationLifecycleHook>(provider => new ContactsDebugCommands(provider));
#endif
    }
}
