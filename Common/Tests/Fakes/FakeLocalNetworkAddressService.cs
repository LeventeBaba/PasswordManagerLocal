using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using System.Net;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeLocalNetworkAddressService : ILocalNetworkAddressService
{
    public IReadOnlyList<string> PreferredLocalHosts { get; set; } = ["192.168.1.10"];
    public IReadOnlyList<IPAddress> MulticastInterfaceAddresses { get; set; } = [IPAddress.Parse("192.168.1.10")];
    public int RemoteEndpointPriority { get; set; } = 5000;
    public Func<string, int>? RemoteEndpointPriorityHandler { get; set; }
    public Func<IPAddress, IPAddress?>? RoutedLocalAddressHandler { get; set; }

    public IReadOnlyList<string> GetPreferredLocalHosts() => PreferredLocalHosts;
    public IReadOnlyList<IPAddress> GetMulticastInterfaceAddresses() => MulticastInterfaceAddresses;

    public IPAddress? GetRoutedLocalAddress(IPAddress remoteAddress) =>
        RoutedLocalAddressHandler?.Invoke(remoteAddress) ?? IPAddress.Parse("192.168.1.10");
    public int GetRemoteEndpointPriority(string host) =>
        RemoteEndpointPriorityHandler?.Invoke(host) ?? RemoteEndpointPriority;
}
