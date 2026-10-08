namespace Wino.Core.Domain.Exceptions;

public class MissingAliasException : System.Exception
{
    public MissingAliasException() : base(Translator.Exception_MissingAlias) { }
}
