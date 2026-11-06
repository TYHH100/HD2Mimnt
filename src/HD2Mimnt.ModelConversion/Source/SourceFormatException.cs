namespace HD2Mimnt.ModelConversion.Source;

public sealed class SourceFormatException : Exception
{
    public SourceFormatException(string message) : base(message)
    {
    }

    public SourceFormatException(string message, Exception inner) : base(message, inner)
    {
    }
}
