using DynaQueueExplorer.Core.Security;

namespace DynaQueueExplorer.Tests;

public class PermissionSetTests
{
    [Theory]
    [InlineData("*", "anything", true)]
    [InlineData("*", "", true)]
    [InlineData("Sales.*", "Sales.OrderService", true)]
    [InlineData("Sales.*", "Billing.OrderService", false)]
    [InlineData("Sales.*", "sales.OrderService", false)]
    [InlineData("nsb.v2.delay-level-?7", "nsb.v2.delay-level-27", true)]
    [InlineData("*Service", "Sales.OrderService", true)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    [InlineData("error", "error.archive", false)]
    public void Glob_matches_case_sensitively(string pattern, string value, bool expected) =>
        Assert.Equal(expected, Glob.IsMatch(pattern, value));

    [Fact]
    public void Deny_by_default() =>
        Assert.False(PermissionSet.Empty.Allows(Permissions.QueuesRead, new ResourceRef("prod", "/", "q")));

    [Fact]
    public void Queue_scoped_grant_is_limited_to_matching_vhost_and_queue()
    {
        var set = new PermissionSet([new Grant(Permissions.MessagesBrowse, "prod", "sales", "Sales.*")]);

        Assert.True(set.Allows(Permissions.MessagesBrowse, new ResourceRef("prod", "sales", "Sales.OrderService")));
        Assert.False(set.Allows(Permissions.MessagesBrowse, new ResourceRef("prod", "sales", "error")));
        Assert.False(set.Allows(Permissions.MessagesBrowse, new ResourceRef("prod", "billing", "Sales.OrderService")));
        Assert.False(set.Allows(Permissions.MessagesBrowse, new ResourceRef("test", "sales", "Sales.OrderService")));
        Assert.False(set.Allows(Permissions.QueuesRead, new ResourceRef("prod", "sales", "Sales.OrderService")));
    }

    [Fact]
    public void Cluster_scoped_action_ignores_vhost_and_queue_patterns()
    {
        var set = new PermissionSet([new Grant(Permissions.NodesRead, "prod", "sales", "nothing")]);

        Assert.True(set.Allows(Permissions.NodesRead, new ResourceRef("prod")));
        Assert.False(set.Allows(Permissions.NodesRead, new ResourceRef("test")));
    }

    [Fact]
    public void Static_shovels_need_a_grant_on_every_vhost()
    {
        var scoped = new PermissionSet([new Grant(Permissions.ShovelsRead, VhostPattern: "sales")]);
        var everywhere = new PermissionSet([new Grant(Permissions.ShovelsRead)]);
        var staticShovel = new ResourceRef("prod", string.Empty);

        Assert.False(scoped.Allows(Permissions.ShovelsRead, staticShovel));
        Assert.True(everywhere.Allows(Permissions.ShovelsRead, staticShovel));
    }

    [Fact]
    public void Wildcard_action_grants_everything_in_scope()
    {
        var set = new PermissionSet([new Grant(Permissions.Any, "test")]);

        Assert.True(set.Allows(Permissions.MessagesBrowse, new ResourceRef("test", "/", "q")));
        Assert.True(set.Allows(Permissions.AuditRead, ResourceRef.Global));
        Assert.False(set.Allows(Permissions.QueuesRead, new ResourceRef("prod", "/", "q")));
        Assert.Equal(Permissions.All.Count(a => Permissions.Scopes[a] != PermissionScope.Global), set.ActionsAnywhereIn("test").Count);
        Assert.Empty(set.ActionsAnywhereIn("prod"));
    }
}
