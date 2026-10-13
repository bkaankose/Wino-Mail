using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.UIKit.Pad.Contacts;

namespace Wino.Mail.UIKit.Pad;

public static partial class PadViewRegistration
{
    static partial void RegisterContactsViews(IServiceCollection services);

    static partial void RegisterContactsViews(IServiceCollection services) => services.AddTransient<PadContactsPageViewController>();
}
