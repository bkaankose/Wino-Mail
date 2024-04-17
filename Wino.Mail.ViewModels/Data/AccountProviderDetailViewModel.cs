using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels.Data
{
    public partial class AccountProviderDetailViewModel : ObservableObject, IAccountProviderDetailViewModel
    {

        [ObservableProperty]
        private MailAccount account;

        public IProviderDetail ProviderDetail { get; set; }

        public Guid StartupEntityId => Account.Id;

        public string StartupEntityTitle => Account.Name;

        public AccountProviderDetailViewModel(IProviderDetail providerDetail, MailAccount account)
        {
            ProviderDetail = providerDetail;
            Account = account;
        }
    }
}
