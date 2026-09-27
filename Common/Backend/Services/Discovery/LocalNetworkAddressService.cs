using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PasswordManagerLocal.Common.Backend.Services.Discovery;

internal sealed class LocalNetworkAddressService : ILocalNetworkAddressService
{
    public IReadOnlyList<string> GetPreferredLocalHosts()
    {
        var candidates = GetAddressCandidates();

        var hasPhysicalPrivateIpv4 = candidates.Any(candidate =>
            !candidate.IsVirtualAdapter &&
            candidate.Address.AddressFamily == AddressFamily.InterNetwork &&
            IsPrivateIpv4(candidate.Address));

        if (hasPhysicalPrivateIpv4)
        {
            candidates = candidates
                .Where(candidate => !candidate.IsVirtualAdapter && candidate.Address.AddressFamily == AddressFamily.InterNetwork)
                .ToList();
        }
        else if (candidates.Any(candidate => !candidate.IsVirtualAdapter))
        {
            candidates = candidates
                .Where(candidate => !candidate.IsVirtualAdapter)
                .ToList();
        }

        if (candidates.Count == 0)
            candidates.AddRange(GetDnsFallbackCandidates());

        if (candidates.Count == 0)
            candidates.AddRange(GetRoutedFallbackCandidates());

        return candidates
            .GroupBy(candidate => candidate.Address.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(candidate => candidate.Priority).First())
            .OrderByDescending(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.Address.ToString(), StringComparer.Ordinal)
            .Select(candidate => candidate.Address.ToString())
            .ToList();
    }


    public IReadOnlyList<IPAddress> GetMulticastInterfaceAddresses()
    {
        var candidates = GetAddressCandidates()
            .Where(candidate => candidate.Address.AddressFamily == AddressFamily.InterNetwork)
            .ToList();

        var physical = candidates
            .Where(candidate => !candidate.IsVirtualAdapter)
            .OrderByDescending(candidate => candidate.Priority)
            .Select(candidate => candidate.Address)
            .Distinct()
            .ToList();

        if (physical.Count > 0)
            return physical;

        return candidates
            .OrderByDescending(candidate => candidate.Priority)
            .Select(candidate => candidate.Address)
            .Distinct()
            .ToList();
    }


    public IPAddress? GetRoutedLocalAddress(IPAddress remoteAddress)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);

        if (remoteAddress.AddressFamily != AddressFamily.InterNetwork || !IsUsableUnicastAddress(remoteAddress))
            return null;

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(remoteAddress, SyncConstants.LocalDiscoveryPort));

            if (socket.LocalEndPoint is IPEndPoint localEndpoint &&
                localEndpoint.Address.AddressFamily == AddressFamily.InterNetwork &&
                IsUsableUnicastAddress(localEndpoint.Address))
                return localEndpoint.Address;
        }
        catch
        {
        }

        return null;
    }


    public int GetRemoteEndpointPriority(string host)
    {
        if (!IPAddress.TryParse(host, out var remoteAddress) || !IsUsableUnicastAddress(remoteAddress))
            return int.MinValue;

        var localNetworks = GetLocalIpv4Networks();
        if (remoteAddress.AddressFamily == AddressFamily.InterNetwork)
        {
            if (localNetworks.Any(network => network.Address.Equals(remoteAddress)))
                return int.MinValue;

            var samePhysicalSubnet = localNetworks.Any(network =>
                !network.IsVirtualAdapter &&
                IsInSameIpv4Subnet(remoteAddress, network.Address, network.Mask));

            if (samePhysicalSubnet)
                return 5000 + GetPrivateAddressPriority(remoteAddress);

            var sameVirtualSubnet = localNetworks.Any(network =>
                network.IsVirtualAdapter &&
                IsInSameIpv4Subnet(remoteAddress, network.Address, network.Mask));

            if (sameVirtualSubnet)
                return 2500 + GetPrivateAddressPriority(remoteAddress);

            if (IsPrivateIpv4(remoteAddress))
                return 100 + GetPrivateAddressPriority(remoteAddress);
        }

        return remoteAddress.AddressFamily == AddressFamily.InterNetwork ? 0 : -100;
    }


    private List<LocalNetworkAddressCandidate> GetAddressCandidates()
    {
        var candidates = new List<LocalNetworkAddressCandidate>();

        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!LocalNetworkInterfaceUtil.IsOperationalForLocalNetwork(networkInterface) ||
                    networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                var properties = networkInterface.GetIPProperties();
                var hasGateway = properties.GatewayAddresses.Any(gateway => IsUsableUnicastAddress(gateway.Address));
                var isVirtualAdapter = IsVirtualOrNonLanAdapter(networkInterface);
                var typePriority = GetNetworkInterfaceTypePriority(networkInterface.NetworkInterfaceType);

                foreach (var addressInfo in properties.UnicastAddresses)
                {
                    var address = addressInfo.Address;
                    if (!IsUsableUnicastAddress(address) || IsWindowsHostOnlyGatewayAddress(address, isVirtualAdapter))
                        continue;

                    var priority = isVirtualAdapter ? -10000 : 10000;
                    if (hasGateway)
                        priority += 4000;

                    priority += typePriority;
                    priority += address.AddressFamily == AddressFamily.InterNetwork ? 2000 : -500;
                    priority += GetPrivateAddressPriority(address);

                    if (OperatingSystem.IsWindows() && addressInfo.PrefixOrigin is PrefixOrigin.Dhcp or PrefixOrigin.Manual)
                        priority += 250;

                    candidates.Add(new LocalNetworkAddressCandidate
                    {
                        Address = address,
                        Priority = priority,
                        IsVirtualAdapter = isVirtualAdapter
                    });
                }
            }
        }
        catch
        {
        }

        return candidates;
    }


    private IReadOnlyList<LocalNetworkAddressCandidate> GetDnsFallbackCandidates()
    {
        var candidates = new List<LocalNetworkAddressCandidate>();

        try
        {
            foreach (var address in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (!IsUsableUnicastAddress(address) || IsWindowsHostOnlyGatewayAddress(address, true))
                    continue;

                candidates.Add(new LocalNetworkAddressCandidate
                {
                    Address = address,
                    Priority = address.AddressFamily == AddressFamily.InterNetwork ? 100 : 50,
                    IsVirtualAdapter = false
                });
            }
        }
        catch
        {
        }

        return candidates;
    }


    private IReadOnlyList<LocalNetworkAddressCandidate> GetRoutedFallbackCandidates() =>
        LocalNetworkInterfaceUtil.GetRoutedLocalAddressFallbacks()
            .Where(IsUsableUnicastAddress)
            .Select(address => new LocalNetworkAddressCandidate
            {
                Address = address,
                Priority = address.AddressFamily == AddressFamily.InterNetwork ? 75 : 25,
                IsVirtualAdapter = false
            })
            .ToList();


    private List<LocalIpv4Network> GetLocalIpv4Networks()
    {
        var networks = new List<LocalIpv4Network>();

        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!LocalNetworkInterfaceUtil.IsOperationalForLocalNetwork(networkInterface) ||
                    networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                var isVirtualAdapter = IsVirtualOrNonLanAdapter(networkInterface);
                foreach (var addressInfo in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    if (addressInfo.Address.AddressFamily != AddressFamily.InterNetwork ||
                        addressInfo.IPv4Mask is null ||
                        !IsUsableUnicastAddress(addressInfo.Address))
                        continue;

                    networks.Add(new LocalIpv4Network
                    {
                        Address = addressInfo.Address,
                        Mask = addressInfo.IPv4Mask,
                        IsVirtualAdapter = isVirtualAdapter
                    });
                }
            }
        }
        catch
        {
        }

        return networks;
    }


    private int GetNetworkInterfaceTypePriority(NetworkInterfaceType interfaceType) =>
        interfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => 3000,
            NetworkInterfaceType.Ethernet => 2500,
            NetworkInterfaceType.GigabitEthernet => 2500,
            NetworkInterfaceType.FastEthernetFx => 2500,
            NetworkInterfaceType.FastEthernetT => 2500,
            NetworkInterfaceType.Ppp => -1000,
            _ => 0
        };


    private bool IsVirtualOrNonLanAdapter(NetworkInterface networkInterface)
    {
        var text = $"{networkInterface.Name} {networkInterface.Description}".ToLowerInvariant();

        return text.Contains("virtual") ||
               text.Contains("vethernet") ||
               text.Contains("hyper-v") ||
               text.Contains("default switch") ||
               text.Contains("wsl") ||
               text.Contains("docker") ||
               text.Contains("vmware") ||
               text.Contains("virtualbox") ||
               text.Contains("vmnet") ||
               text.Contains("host-only") ||
               text.Contains("loopback") ||
               text.Contains("npcap") ||
               text.Contains("bluetooth") ||
               text.Contains("vpn") ||
               text.Contains("tap") ||
               text.Contains("tun") ||
               text.Contains("tailscale") ||
               text.Contains("zerotier") ||
               text.Contains("wireguard") ||
               text.Contains("pseudo");
    }


    private bool IsWindowsHostOnlyGatewayAddress(IPAddress address, bool isVirtualAdapter)
    {
        if (!isVirtualAdapter || address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[3] == 1;
    }


    private bool IsUsableUnicastAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.Broadcast) ||
            address.Equals(IPAddress.IPv6Any))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !IsApipaIpv4(address);

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return false;

        return !address.IsIPv6LinkLocal && !address.IsIPv6Multicast && !address.IsIPv6SiteLocal;
    }


    private bool IsApipaIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }


    private bool IsInSameIpv4Subnet(IPAddress remoteAddress, IPAddress localAddress, IPAddress mask)
    {
        if (remoteAddress.AddressFamily != AddressFamily.InterNetwork ||
            localAddress.AddressFamily != AddressFamily.InterNetwork ||
            mask.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var remoteBytes = remoteAddress.GetAddressBytes();
        var localBytes = localAddress.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();

        if (remoteBytes.Length != 4 || localBytes.Length != 4 || maskBytes.Length != 4)
            return false;

        for (var i = 0; i < 4; i++)
        {
            if ((remoteBytes[i] & maskBytes[i]) != (localBytes[i] & maskBytes[i]))
                return false;
        }

        return true;
    }


    private int GetPrivateAddressPriority(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes.Length != 4)
                return 0;

            if (bytes[0] == 192 && bytes[1] == 168)
                return 500;

            if (bytes[0] == 10)
                return 400;

            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return 250;

            return -500;
        }

        return IsPrivateIpv6(address) ? 100 : -500;
    }


    private bool IsPrivateIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 &&
               (bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168));
    }


    private bool IsPrivateIpv6(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return false;

        var bytes = address.GetAddressBytes();
        return bytes.Length == 16 && (bytes[0] & 0xFE) == 0xFC;
    }
}
