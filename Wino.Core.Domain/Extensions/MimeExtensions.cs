using MimeKit;
using Wino.Domain;
using Wino.Domain.Entities;

namespace Wino.Domain.Extensions
{
    public static class MimeExtensions
    {


        public static AddressInformation ToAddressInformation(this MailboxAddress address)
        {
            if (address == null)
                return new AddressInformation() { Name = Translator.UnknownSender, Address = Translator.UnknownAddress };

            if (string.IsNullOrEmpty(address.Name))
                address.Name = address.Address;

            return new AddressInformation() { Name = address.Name, Address = address.Address };
        }
    }
}
