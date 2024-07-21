using Microsoft.Extensions.DependencyInjection;

namespace Wino.Mail.ViewModels
{
    public static class ViewModelContainerSetup
    {
        public static void RegisterViewModels(this IServiceCollection services)
        {
            services.AddSingleton(typeof(AppShellViewModel));
            services.AddTransient(typeof(SettingsDialogViewModel));
            services.AddTransient(typeof(PersonalizationPageViewModel));
            services.AddTransient(typeof(SettingOptionsPageViewModel));
            services.AddTransient(typeof(MailListPageViewModel));
            services.AddTransient(typeof(MailRenderingPageViewModel));
            services.AddTransient(typeof(AccountManagementViewModel));
            services.AddTransient(typeof(WelcomePageViewModel));
            services.AddTransient(typeof(AboutPageViewModel));
            services.AddTransient(typeof(ComposePageViewModel));
            services.AddTransient(typeof(IdlePageViewModel));
            services.AddTransient(typeof(SettingsPageViewModel));
            services.AddTransient(typeof(NewAccountManagementPageViewModel));
            services.AddTransient(typeof(AccountDetailsPageViewModel));
            services.AddTransient(typeof(SignatureManagementPageViewModel));
            services.AddTransient(typeof(MessageListPageViewModel));
            services.AddTransient(typeof(ReadingPanePageViewModel));
            services.AddTransient(typeof(MergedAccountDetailsPageViewModel));
            services.AddTransient(typeof(LanguageTimePageViewModel));
        }
    }
}
