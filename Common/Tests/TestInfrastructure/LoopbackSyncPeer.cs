using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Sync.Tcp;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure;

internal sealed class LoopbackSyncPeer(TcpClient client, SslStream stream) : IAsyncDisposable
{
    public SslStream Stream { get; } = stream;

    public static async Task<LoopbackSyncPeer> ConnectAsync(
        IPEndPoint endpoint, IDeviceIdentityService server, IDeviceIdentityService? clientIdentity, CancellationToken ct,
        IPAddress? sourceAddress = null)
    {
        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        SslStream? stream = null;
        try
        {
            if (sourceAddress is not null)
                client.Client.Bind(new IPEndPoint(sourceAddress, 0));
            await client.ConnectAsync(endpoint.Address, endpoint.Port, ct);
            stream = new SslStream(client.GetStream());
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = endpoint.Address.ToString(),
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is not null && cert.GetRawCertData().AsSpan().SequenceEqual(server.Certificate.RawData)
            };
            if (clientIdentity is not null)
            {
                options.ClientCertificates = new X509CertificateCollection { clientIdentity.Certificate };
                options.LocalCertificateSelectionCallback = (_, _, _, _, _) => clientIdentity.Certificate;
            }

            await stream.AuthenticateAsClientAsync(options, ct);
            return new LoopbackSyncPeer(client, stream);
        }
        catch
        {
            stream?.Dispose();
            client.Dispose();
            throw;
        }
    }

    public static async Task<LoopbackSyncPeer> AcceptAsync(
        TcpListener listener, IDeviceIdentityService server, IDeviceIdentityService expectedClient, CancellationToken ct)
    {
        var client = await listener.AcceptTcpClientAsync(ct);
        var stream = new SslStream(client.GetStream());
        try
        {
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = server.Certificate,
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is not null && cert.GetRawCertData().AsSpan().SequenceEqual(expectedClient.Certificate.RawData)
            }, ct);
            return new LoopbackSyncPeer(client, stream);
        }
        catch
        {
            stream.Dispose();
            client.Dispose();
            throw;
        }
    }

    public static void RequireStressTestsEnabled()
    {
        if (Environment.GetEnvironmentVariable("PASSWORDMANAGERLOCAL_SECURITY_STRESS") != "1")
            Assert.Inconclusive("Set PASSWORDMANAGERLOCAL_SECURITY_STRESS=1 to exercise large or platform-dependent wire limits.");
    }

    public static async Task<SyncTcpFrame> ReadFrameAsync(Stream stream, SyncTcpMessageType expected, CancellationToken ct)
    {
        var frame = await SyncTcpFrameIo.ReadAsync(stream, ct);
        Assert.IsNotNull(frame, $"The peer closed before sending {expected}.");
        Assert.AreEqual(expected, frame.Type);
        return frame;
    }

    public static async Task<SyncTcpFrame?> ReadUntilCloseAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            return await SyncTcpFrameIo.ReadAsync(stream, ct);
        }
        catch (IOException ex) when (ex is not EndOfStreamException)
        {
            // Some TLS providers report a peer abort as an I/O error instead of clean EOF.
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        using (client)
            await Stream.DisposeAsync();
    }
}
