using Wino.Mail.Controls.Core;

namespace Wino.Mail.Controls.Playground.Models;

public sealed record SampleContact(string Name, string Address, string? LocalImagePath = null) : IContactPicture;
