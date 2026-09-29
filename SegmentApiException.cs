namespace distanceExport;

public sealed class SegmentApiException : Exception
{
    public SegmentApiException(string message) : base(message) { }
    public SegmentApiException(string message, Exception inner) : base(message, inner) { }
}
