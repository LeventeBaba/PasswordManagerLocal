using System.Net;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface ILocalNetworkAddressService
{
    IReadOnlyList<string> GetPreferredLocalHosts();
    IReadOnlyList<IPAddress> GetMulticastInterfaceAddresses();
    IPAddress? GetRoutedLocalAddress(IPAddress remoteAddress);
    int GetRemoteEndpointPriority(string host);
}
