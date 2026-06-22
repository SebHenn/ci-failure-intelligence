using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CiFail.Core.Configuration;
using CiFail.Server;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;

namespace CiFail.Server.Tests;

/// <summary>
/// mTLS (R20): serve can require a client certificate that chains to a configured CA. A client
/// presenting such a cert connects; one without a cert is rejected at the TLS handshake.
/// Certificates are generated in-process — no external PKI or Docker needed.
/// </summary>
public sealed class ServeMutualTlsTests : IAsyncLifetime
{
    private readonly string _home;
    private readonly string _caPemPath;
    private readonly string _serverPfxPath;
    private X509Certificate2 _clientCert = null!;
    private WebApplication? _app;
    private string _baseUrl = null!;

    public ServeMutualTlsTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cifail-mtls-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _home = Path.Combine(dir, "home");
        _caPemPath = Path.Combine(dir, "ca.pem");
        _serverPfxPath = Path.Combine(dir, "server.pfx");
        Environment.SetEnvironmentVariable("CIFAIL_HOME", _home);
    }

    public async Task InitializeAsync()
    {
        var (ca, server, client) = CreateCertChain();
        File.WriteAllText(_caPemPath, ca.ExportCertificatePem());
        File.WriteAllBytes(_serverPfxPath, server.Export(X509ContentType.Pfx));
        // Re-import the client cert through a PFX so its private key is usable in the handshake.
        _clientCert = new X509Certificate2(client.Export(X509ContentType.Pfx));

        _app = CiFailServer.Build(new ServeOptions
        {
            Url = "http://127.0.0.1:0",
            Database = new DatabaseConfig { Provider = "sqlite" },
            ClientCaPath = _caPemPath,
            TlsCertPath = _serverPfxPath,
        });
        await _app.StartAsync();
        _baseUrl = _app.Urls.First();
        _baseUrl.Should().StartWith("https://");
    }

    [Fact]
    public async Task A_client_with_a_trusted_cert_connects()
    {
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true, // self-signed server cert
        };
        handler.ClientCertificates.Add(_clientCert);
        using var http = new HttpClient(handler) { BaseAddress = new Uri(_baseUrl.TrimEnd('/') + "/") };

        var response = await http.GetAsync("healthz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_client_without_a_cert_is_rejected_at_the_handshake()
    {
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(_baseUrl.TrimEnd('/') + "/") };

        var act = async () => await http.GetAsync("healthz");
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    public async Task DisposeAsync()
    {
        _clientCert?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        try
        {
            var dir = Path.GetDirectoryName(_caPemPath)!;
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* best-effort temp cleanup */ }
    }

    /// <summary>A self-signed CA, a self-signed server cert (CN=localhost), and a client cert the CA signs.</summary>
    private static (X509Certificate2 Ca, X509Certificate2 Server, X509Certificate2 Client) CreateCertChain()
    {
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddDays(1);

        using var caKey = RSA.Create(2048);
        var caReq = new CertificateRequest("CN=cifail-test-ca", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var ca = caReq.CreateSelfSigned(notBefore, notAfter);

        using var serverKey = RSA.Create(2048);
        var serverReq = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        serverReq.CertificateExtensions.Add(san.Build());
        serverReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth
        var server = serverReq.CreateSelfSigned(notBefore, notAfter);

        using var clientKey = RSA.Create(2048);
        var clientReq = new CertificateRequest("CN=cifail-test-client", clientKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        clientReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") }, false)); // clientAuth
        var serial = Guid.NewGuid().ToByteArray();
        using var clientPub = clientReq.Create(ca, notBefore, notAfter, serial);
        var client = clientPub.CopyWithPrivateKey(clientKey);

        return (ca, server, client);
    }
}
