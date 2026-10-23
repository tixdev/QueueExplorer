using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace DynaQueueExplorer.Core.Clusters;

/// <summary>
/// Server certificate validation for a cluster, shared by every broker client (RabbitMQ HTTP and AMQP, IBM MQ).
/// Without a configured CA the platform validation applies; with one, the chain must end in that CA and the
/// host name must match. Validation is never switched off.
/// </summary>
public static class ClusterTls
{
    public static RemoteCertificateValidationCallback? CreateValidator(ClusterOptions cluster, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(cluster.CaCertificatePath))
            return null;

        var path = Path.IsPathRooted(cluster.CaCertificatePath)
            ? cluster.CaCertificatePath
            : Path.GetFullPath(Path.Combine(contentRoot, cluster.CaCertificatePath));
        if (!File.Exists(path))
            throw new FileNotFoundException($"CA certificate for cluster '{cluster.Name}' not found.", path);
        var authority = X509CertificateLoader.LoadCertificateFromFile(path);

        return (_, certificate, _, errors) =>
        {
            if (errors == SslPolicyErrors.None)
                return true;
            // Only an untrusted root is acceptable to re-check; a wrong host name or a missing certificate is not.
            if (certificate is null || errors != SslPolicyErrors.RemoteCertificateChainErrors)
                return false;

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(authority);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            using var server = new X509Certificate2(certificate);
            return chain.Build(server);
        };
    }
}
