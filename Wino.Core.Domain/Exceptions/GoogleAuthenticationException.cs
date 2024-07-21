namespace Wino.Domain.Exceptions
{
    public class GoogleAuthenticationException : Exception
    {
        public GoogleAuthenticationException(string message) : base(message) { }
    }
}
