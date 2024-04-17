using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Messages.Mails;

namespace Wino.Mail.ViewModels
{
    public partial class IdlePageViewModel : BaseViewModel, IRecipient<SelectedMailItemsChanged>
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSelectedItems))]
        [NotifyPropertyChangedFor(nameof(SelectedMessageText))]
        private int selectedItemCount;

        public bool HasSelectedItems => SelectedItemCount > 0;

        public string SelectedMessageText => HasSelectedItems ? string.Format(Translator.MailsSelected, SelectedItemCount) : Translator.NoMailSelected;

        public IdlePageViewModel(IDialogService dialogService) : base(dialogService) { }

        public void Receive(SelectedMailItemsChanged message)
        {
            SelectedItemCount = message.SelectedItemCount;
        }

        public override void OnNavigatedTo(NavigationMode mode, object parameters)
        {
            base.OnNavigatedTo(mode, parameters);

            if (parameters != null && parameters is int selectedItemCount)
                SelectedItemCount = selectedItemCount;
            else
                SelectedItemCount = 0;
        }
    }
}
