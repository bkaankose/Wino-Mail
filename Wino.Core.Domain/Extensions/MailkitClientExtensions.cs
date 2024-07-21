namespace Wino.Domain.Extensions
{
    public static class MailkitClientExtensions
    {
        public static uint ResolveUid(string mailCopyId)
        {
            var splitted = mailCopyId.Split(Constants.MailCopyUidSeparator);

            if (splitted.Length > 1 && uint.TryParse(splitted[1], out uint parsedUint)) return parsedUint;

            throw new ArgumentOutOfRangeException(nameof(mailCopyId), mailCopyId, "Invalid mailCopyId format.");
        }
    }
}
