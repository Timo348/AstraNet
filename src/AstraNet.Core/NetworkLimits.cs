namespace AstraNet.Core;

public static class NetworkLimits
{
    public const int MaxMessageSize = 1024 * 1024;
}

public sealed class NetworkProtocolException : IOException
{
    public NetworkProtocolException(string message) : base(message) { }
    public NetworkProtocolException(string message, Exception innerException) : base(message, innerException) { }
}
