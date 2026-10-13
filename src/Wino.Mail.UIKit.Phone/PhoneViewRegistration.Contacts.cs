using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.UIKit.Phone.Contacts;

namespace Wino.Mail.UIKit.Phone;

public static partial class PhoneViewRegistration
{
    static partial void RegisterContactsViews(IServiceCollection services);

    static partial void RegisterContactsViews(IServiceCollection services) => services.AddTransient<PhoneContactsPageViewController>();
}
