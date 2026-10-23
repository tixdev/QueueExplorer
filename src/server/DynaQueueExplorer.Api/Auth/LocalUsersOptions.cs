namespace DynaQueueExplorer.Api.Auth;

/// <summary>
/// Hardcoded users for the test phase. To be replaced by an OIDC identity provider;
/// groups then come from the provider's group claims and map onto the same role assignments.
/// </summary>
public sealed class LocalUser
{
    public required string Username { get; set; }
    public required string Password { get; set; }
    public string? DisplayName { get; set; }
    public List<string> Groups { get; set; } = [];
}

public sealed class LocalUsersOptions
{
    public const string SectionName = "LocalUsers";

    public List<LocalUser> Users { get; set; } = [];
}
