using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels.Data
{
    public partial class MergedAccountProviderDetailViewModel : ObservableObject, IAccountProviderDetailViewModel
    {
        public List<AccountProviderDetailViewModel> HoldingAccounts { get; }
        public MergedInbox MergedInbox { get; }

        public string AccountAddresses => string.Join(", ", HoldingAccounts.Select(a => a.Account.Address));

        public Guid StartupEntityId => MergedInbox.Id;

        public string StartupEntityTitle => MergedInbox.Name;

        public int Order => 0;

        public IProviderDetail ProviderDetail { get; set; }

        public string StartupEntityAddresses => AccountAddresses;

        public int HoldingAccountCount => HoldingAccounts.Count;

        public MergedAccountProviderDetailViewModel(MergedInbox mergedInbox, List<AccountProviderDetailViewModel> holdingAccounts)
        {
            MergedInbox = mergedInbox;
            HoldingAccounts = holdingAccounts;
        }
    }
}
