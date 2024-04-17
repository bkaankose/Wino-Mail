using System.Collections.Specialized;
using System.Linq;
using MimeKit;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.MailItem
{
    public class DraftCreationOptions
    {
        public MimeMessage ReferenceMimeMessage { get; set; }
        public MailCopy ReferenceMailCopy { get; set; }
        public DraftCreationReason Reason { get; set; }

        #region Mailto Protocol Related Stuff

        public const string MailtoSubjectParameterKey = "subject";
        public const string MailtoBodyParameterKey = "body";
        public const string MailtoToParameterKey = "mailto";
        public const string MailtoCCParameterKey = "cc";
        public const string MailtoBCCParameterKey = "bcc";

        public NameValueCollection MailtoParameters { get; set; }

        private bool IsMailtoParameterExists(string parameterKey)
            => MailtoParameters != null
            && MailtoParameters.AllKeys.Contains(parameterKey);

        public bool TryGetMailtoValue(string key, out string value)
        {
            bool valueExists = IsMailtoParameterExists(key);

            value = valueExists ? MailtoParameters[key] : string.Empty;

            return valueExists;
        }

        #endregion
    }
}
