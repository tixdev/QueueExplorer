using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DynaQueueExplorer.Api.Security;
using DynaQueueExplorer.Core.Auditing;
using DynaQueueExplorer.Core.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace DynaQueueExplorer.Api.Auth;

public static class AuthEndpoints
{
    public const string LoginRateLimitPolicy = "login";

    public sealed record LoginRequest(string Username, string Password);

    public sealed record ClusterAccess(string Name, string? Description, IReadOnlyList<string> Actions);

    public sealed record MeResponse(
        string Username,
        string DisplayName,
        IReadOnlyList<string> Groups,
        IReadOnlyList<string> GlobalActions,
        IReadOnlyList<ClusterAccess> Clusters);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapPost("/login", async (LoginRequest request, HttpContext http, IOptions<LocalUsersOptions> users, IAuditLog audit) =>
        {
            var user = users.Value.Users.FirstOrDefault(u => string.Equals(u.Username, request.Username, StringComparison.OrdinalIgnoreCase));
            if (user is null || !FixedTimeEquals(user.Password, request.Password))
            {
                await audit.WriteAsync(new AuditRecord(request.Username, "auth.login", AuditOutcome.Denied), http.RequestAborted);
                return Results.Problem("Invalid username or password.", statusCode: StatusCodes.Status401Unauthorized);
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, user.Username),
                new(CurrentUser.DisplayNameClaim, user.DisplayName ?? user.Username),
            };
            claims.AddRange(user.Groups.Select(g => new Claim(CurrentUser.GroupClaim, g)));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));

            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            await audit.WriteAsync(new AuditRecord(user.Username, "auth.login", AuditOutcome.Success), http.RequestAborted);
            return Results.NoContent();
        }).RequireRateLimiting(LoginRateLimitPolicy);

        auth.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        // Single bootstrap call: identity, visible clusters and the actions allowed on each.
        app.MapGet("/api/me", async (CurrentUser user, DynaQueueExplorer.RabbitMq.Snapshots.ClusterClients clients, CancellationToken ct) =>
        {
            var permissions = await user.GetPermissionsAsync(ct);
            var clusters = clients.Clusters
                .Select(c => new ClusterAccess(c.Name, c.Description, permissions.ActionsAnywhereIn(c.Name)))
                .Where(c => c.Actions.Count > 0)
                .ToList();
            var global = Permissions.All
                .Where(a => Permissions.Scopes[a] == PermissionScope.Global && permissions.Allows(a, ResourceRef.Global))
                .ToList();
            return new MeResponse(user.Username, user.DisplayName, user.Groups, global, clusters);
        }).RequireAuthorization();
    }

    private static bool FixedTimeEquals(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(actual)));
}
