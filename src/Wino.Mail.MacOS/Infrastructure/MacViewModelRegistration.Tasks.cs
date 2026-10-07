using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.MacOS.Views.ToDo;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>To Do mode views. Owned by the Tasks feature; ToDoPageViewModel is the shared singleton registered in MacViewModelRegistration.cs.</summary>
public static partial class MacViewModelRegistration
{
    static partial void RegisterTasksViews(IServiceCollection services)
    {
        services.AddTransient<ToDoPageViewController>();
    }
}
