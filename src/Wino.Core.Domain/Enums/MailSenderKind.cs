namespace Wino.Core.Domain.Enums;

/// <summary>What kind of sender a mail comes from, as <see cref="Models.Contacts.MailSenderClassifier"/> decides from the address and display name.</summary>
public enum MailSenderKind
{
    Any,
    Person,
    Service,
    Newsletter,
}
