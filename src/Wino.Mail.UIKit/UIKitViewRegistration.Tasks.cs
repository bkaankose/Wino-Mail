using Microsoft.Extensions.DependencyInjection;

namespace Wino.Mail.UIKit;

/// <summary>
/// To Do pages shared by both idioms. The page is an idiom subclass (PhoneViewRegistration/PadViewRegistration) and the
/// task detail is created by the page, so nothing shared needs a container registration yet.
/// </summary>
public static partial class UIKitViewRegistration
{
    static partial void RegisterTasksViews(IServiceCollection services);
}
