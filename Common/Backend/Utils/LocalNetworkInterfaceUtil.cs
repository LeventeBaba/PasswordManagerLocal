using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PasswordManagerLocal.Common.Backend.Utils;

internal static class LocalNetworkInterfaceUtil
{
    private static readonly IPEndPoint[] RouteProbeEndpoints =
    [
        new(IPAddress.Parse("192.0.2.1"), 9),
        new(IPAddress.Parse("2001:db8::1"), 9)
    ];

    public static bool IsOperationalForLocalNetwork(NetworkInterface networkInterface)
    {
        var status = networkInterface.OperationalStatus;
        return status == OperationalStatus.Up ||
               (OperatingSystem.IsAndroid() && status == OperationalStatus.Unknown);
    }

    public static IReadOnlyList<IPAddress> GetRoutedLocalAddressFallbacks()
    {
        var addresses = new List<IPAddress>();

        foreach (var endpoint in RouteProbeEndpoints)
            TryAddRoutedLocalAddress(endpoint, addresses);

        return addresses
            .Distinct()
            .ToList();
    }

    private static void TryAddRoutedLocalAddress(IPEndPoint endpoint, ICollection<IPAddress> addresses)
    {
        try
        {
            using var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(endpoint);

            if (socket.LocalEndPoint is IPEndPoint localEndpoint)
                addresses.Add(localEndpoint.Address);
        }
        catch
        {
        }
    }
}
