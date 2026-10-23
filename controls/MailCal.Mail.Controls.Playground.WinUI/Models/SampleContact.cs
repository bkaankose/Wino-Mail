using MailCal.Mail.Controls.Core;

namespace MailCal.Mail.Controls.Playground.Models;

public sealed record SampleContact(string Name, string Address, string? LocalImagePath = null) : IContactPicture;
