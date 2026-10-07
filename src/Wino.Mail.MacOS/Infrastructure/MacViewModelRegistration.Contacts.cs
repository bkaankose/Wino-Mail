using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.MacOS.Views.Contacts;
using Wino.Mail.ViewModels;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Contacts mode views and ViewModels. Owned by the Contacts feature. ContactsPageViewModel is a shared singleton.</summary>
public static partial class MacViewModelRegistration
{
    static partial void RegisterContactsViews(IServiceCollection services)
    {
        services.AddTransient<ContactEditPageViewModel>();
        services.AddTransient<ContactsPageViewController>();
        services.AddTransient<ContactEditPageViewController>();
    }
}
