namespace HD2Mimnt.ModelConversion.Ias;

/// <summary>An IAS document or binary violates its format contract.</summary>
public sealed class IasFormatException : Exception
{
    public IasFormatException(string message) : base(message)
    {
    }
}
