using System;

namespace Wino.Core.Domain.Extensions
{
    public static class MimeExtensions
    {
        public static string GetBase64MimeMessage(this MimeKit.MimeMessage message)
        {
            using System.IO.MemoryStream memoryStream = new();
            message.WriteTo(MimeKit.FormatOptions.Default, memoryStream);
            byte[] buffer = memoryStream.GetBuffer();
            int count = (int)memoryStream.Length;

            return Convert.ToBase64String(buffer);
        }

        public static MimeKit.MimeMessage GetMimeMessageFromBase64(this string base64)
            => MimeKit.MimeMessage.Load(new System.IO.MemoryStream(Convert.FromBase64String(base64)));
    }
}
