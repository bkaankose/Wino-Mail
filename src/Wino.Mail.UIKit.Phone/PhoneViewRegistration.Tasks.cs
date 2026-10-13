using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.UIKit.Phone.ToDo;

namespace Wino.Mail.UIKit.Phone;

public static partial class PhoneViewRegistration
{
    static partial void RegisterTasksViews(IServiceCollection services);

    static partial void RegisterTasksViews(IServiceCollection services) => services.AddTransient<PhoneToDoPageViewController>();
}
