namespace Wino.Core.Domain.Enums;

/// <summary>What a mention reads in the message body. A part the person does not have is left out.</summary>
public enum ComposerMentionFormat
{
    NameAndAddress,
    Name,
    Address
}
