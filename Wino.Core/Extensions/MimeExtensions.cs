using System.IO;
using System.Text;
using Google.Apis.Gmail.v1.Data;
using MimeKit;
using MimeKit.IO;
using MimeKit.IO.Filters;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;

namespace Wino.Core.Extensions
{
    public static class MimeExtensions
    {
        /// <summary>
        /// Returns MimeKit.MimeMessage instance for this GMail Message's Raw content.
        /// </summary>
        /// <param name="message">GMail message.</param>
        public static MimeMessage GetGmailMimeMessage(this Message message)
        {
            if (message == null || message.Raw == null)
                return null;

            // Gmail raw is not base64 but base64Safe. We need to remove this HTML things.
            var base64Encoded = message.Raw.Replace(",", "=").Replace("-", "+").Replace("_", "/");

            byte[] bytes = Encoding.ASCII.GetBytes(base64Encoded);

            var stream = new MemoryStream(bytes);

            // This method will dispose outer stream.

            using (stream)
            {
                using var filtered = new FilteredStream(stream);
                filtered.Add(DecoderFilter.Create(ContentEncoding.Base64));

                return MimeMessage.Load(filtered);
            }
        }

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
