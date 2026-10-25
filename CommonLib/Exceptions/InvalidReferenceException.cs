namespace Aloe.CommonLib.Exceptions;

/// <summary>A source reference variable is invalid or no longer identifies a live heap object.</summary>
public sealed class InvalidReferenceException : SystemException
{
    public InvalidReferenceException(string message) : base(message) { }
}
