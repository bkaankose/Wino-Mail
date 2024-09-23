using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Core.ViewModels
{
    public class CoreBaseViewModel : ObservableRecipient, INavigationAware
    {
        protected IDialogService DialogService { get; }

        private IDispatcher _dispatcher;
        public IDispatcher Dispatcher
        {
            get
            {
                return _dispatcher;
            }
            set
            {
                _dispatcher = value;

                if (value != null)
                {
                    OnDispatcherAssigned();
                }
            }
        }

        public virtual void OnNavigatedTo(NavigationMode mode, object parameters) { IsActive = true; }

        public virtual void OnNavigatedFrom(NavigationMode mode, object parameters) { IsActive = false; }

        public CoreBaseViewModel(IDialogService dialogService) => DialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));

        public async Task ExecuteUIThread(Action action) => await Dispatcher?.ExecuteOnUIThread(action);
        public void ReportUIChange<TMessage>(TMessage message) where TMessage : class, IUIMessage => Messenger.Send(message);

        protected virtual void OnDispatcherAssigned() { }
    }
}
