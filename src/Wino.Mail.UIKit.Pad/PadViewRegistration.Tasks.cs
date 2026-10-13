using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.UIKit.Pad.ToDo;

namespace Wino.Mail.UIKit.Pad;

public static partial class PadViewRegistration
{
    static partial void RegisterTasksViews(IServiceCollection services);

    static partial void RegisterTasksViews(IServiceCollection services) => services.AddTransient<PadToDoPageViewController>();
}
