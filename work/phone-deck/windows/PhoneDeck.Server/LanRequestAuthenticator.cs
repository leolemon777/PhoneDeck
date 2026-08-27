using System.Net;
using System.Security.Cryptography;
using System.Text;

internal static class LanRequestAuthenticator
{
    internal static bool IsAuthorized(int localPort, string? suppliedToken, string expectedToken)
    {
        if (localPort != 8766)
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(suppliedToken))
        {
            return false;
        }
        var supplied = Encoding.UTF8.GetBytes(suppliedToken);
        var expected = Encoding.UTF8.GetBytes(expectedToken);
        return supplied.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    internal static bool IsUsbPairingRequest(int localPort, IPAddress? remoteAddress) =>
        localPort == 8765
        && remoteAddress is not null
        && IPAddress.IsLoopback(remoteAddress);
}
