using System.IO.Hashing;
using System.Security.Claims;
using System.Text;
using DynaQueueExplorer.Core.Security;

namespace DynaQueueExplorer.Api.Security;

/// <summary>The authenticated user of the current request and its (lazily resolved) permissions.</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor, IPermissionStore store)
{
    public const string GroupClaim = "dqe:group";
    public const string DisplayNameClaim = "dqe:display_name";

    private PermissionSet? _permissions;
    private string? _fingerprint;

    private ClaimsPrincipal Principal => accessor.HttpContext?.User ?? new ClaimsPrincipal();

    public string Username => Principal.Identity?.Name ?? throw new InvalidOperationException("No authenticated user.");

    public string DisplayName => Principal.FindFirstValue(DisplayNameClaim) ?? Username;

    public IReadOnlyList<string> Groups => Principal.FindAll(GroupClaim).Select(c => c.Value).ToList();

    public async Task<PermissionSet> GetPermissionsAsync(CancellationToken ct) =>
        _permissions ??= await store.GetPermissionsAsync(Username, Groups, ct);

    /// <summary>
    /// Stable hash of the effective grants. Users with the same permissions share cached, filtered responses.
    /// </summary>
    public async Task<string> GetPermissionFingerprintAsync(CancellationToken ct)
    {
        if (_fingerprint is not null)
            return _fingerprint;

        var permissions = await GetPermissionsAsync(ct);
        var canonical = string.Join('\n', permissions.Grants
            .Select(g => $"{g.Action}\t{g.ClusterPattern}\t{g.VhostPattern}\t{g.QueuePattern}")
            .Order(StringComparer.Ordinal));
        return _fingerprint = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(canonical)).ToString("x16");
    }
}
