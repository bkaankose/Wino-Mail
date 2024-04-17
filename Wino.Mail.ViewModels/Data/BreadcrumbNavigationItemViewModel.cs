using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Messages.Navigation;

namespace Wino.Mail.ViewModels.Data
{
    public class BreadcrumbNavigationItemViewModel : ObservableObject
    {
        public BreadcrumbNavigationRequested Request { get; set; }

        public BreadcrumbNavigationItemViewModel(BreadcrumbNavigationRequested request, bool isActive)
        {
            Request = request;
            Title = request.PageTitle;

            this.isActive = isActive;
        }

        private string title;
        public string Title
        {
            get => title;
            set => SetProperty(ref title, value);
        }

        private bool isActive;

        public bool IsActive
        {
            get => isActive;
            set => SetProperty(ref isActive, value);
        }
    }
}
