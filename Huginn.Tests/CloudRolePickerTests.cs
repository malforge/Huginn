using System.Linq;
using Huginn.Models;
using Huginn.ViewModels;
using Xunit;

namespace Huginn.Tests;

public sealed class CloudRolePickerTests
{
    private static QueryTable Seen(params (string Role, string Requests)[] rows) =>
        new(["cloud_RoleName", "requests", "other"], [.. rows.Select(r => (System.Collections.Generic.IReadOnlyList<string>)[r.Role, r.Requests, "3"])]);

    [Fact]
    public void The_roles_reporting_to_a_resource_are_read_busiest_first_without_the_unnamed_one()
    {
        var roles = CloudRoles.From(Seen(("prod-go-web-eurw", "900000"), ("", "40"), ("prod-go-app-mobile-eurw", "120000")));

        Assert.Equal(["prod-go-web-eurw", "prod-go-app-mobile-eurw"], roles.Select(r => r.Name));
        Assert.Equal(900000, roles[0].Requests);
        Assert.Equal(3, roles[0].Other);
    }

    [Fact]
    public void The_roles_already_chosen_are_ticked()
    {
        SelectableComponent component = new() { Roles = "prod-go-app-mobile-eurw" };

        component.ShowSeenRoles(CloudRoles.From(Seen(("prod-go-web-eurw", "9"), ("prod-go-app-mobile-eurw", "5"))));

        Assert.Equal([false, true], component.SeenRoles.Select(r => r.IsSelected));
    }

    [Fact]
    public void A_chosen_role_that_has_not_reported_lately_is_still_listed_ticked()
    {
        SelectableComponent component = new() { Roles = "prod-go-worker-eurw" };

        component.ShowSeenRoles(CloudRoles.From(Seen(("prod-go-web-eurw", "9"))));

        SelectableRole quiet = component.SeenRoles.Single(r => r.Name == "prod-go-worker-eurw");
        Assert.True(quiet.IsSelected);
        Assert.Null(quiet.Seen);
    }

    [Fact]
    public void Ticking_and_unticking_a_role_edits_the_chosen_roles()
    {
        SelectableComponent component = new() { Roles = "typed-by-hand" };
        component.ShowSeenRoles(CloudRoles.From(Seen(("prod-go-web-eurw", "9"), ("prod-go-app-mobile-eurw", "5"))));

        component.SeenRoles.Single(r => r.Name == "prod-go-app-mobile-eurw").IsSelected = true;
        Assert.Equal(["typed-by-hand", "prod-go-app-mobile-eurw"], component.RoleList);

        component.SeenRoles.Single(r => r.Name == "typed-by-hand").IsSelected = false;
        Assert.Equal(["prod-go-app-mobile-eurw"], component.RoleList);
    }

    [Fact]
    public void Unticking_a_role_keeps_it_in_the_list_so_nothing_moves()
    {
        SelectableComponent component = new() { Roles = "prod-go-worker-eurw" };
        component.ShowSeenRoles(CloudRoles.From(Seen(("prod-go-web-eurw", "9"))));

        component.SeenRoles.Single(r => r.Name == "prod-go-worker-eurw").IsSelected = false;

        Assert.Equal(2, component.SeenRoles.Count);
        Assert.Empty(component.RoleList);
    }

    [Fact]
    public void Typing_the_roles_updates_the_ticks()
    {
        SelectableComponent component = new();
        component.ShowSeenRoles(CloudRoles.From(Seen(("prod-go-web-eurw", "9"), ("prod-go-app-mobile-eurw", "5"))));

        component.Roles = "prod-go-app-mobile-eurw";

        Assert.Equal([false, true], component.SeenRoles.Select(r => r.IsSelected));
    }

    /// <summary>The monitor's filter matches role names exactly, so a name in another case matches nothing.</summary>
    [Fact]
    public void A_role_typed_in_another_case_is_listed_as_not_seen_rather_than_ticking_the_real_one()
    {
        SelectableComponent component = new();
        component.ShowSeenRoles(CloudRoles.From(Seen(("prod-go-app-mobile-eurw", "5"))));

        component.Roles = "PROD-GO-APP-MOBILE-EURW";

        Assert.False(component.SeenRoles.Single(r => r.Name == "prod-go-app-mobile-eurw").IsSelected);
        SelectableRole typed = component.SeenRoles.Single(r => r.Name == "PROD-GO-APP-MOBILE-EURW");
        Assert.True(typed.IsSelected);
        Assert.Null(typed.Seen);
    }

    [Fact]
    public void A_role_typed_after_the_list_loaded_gets_its_own_row()
    {
        SelectableComponent component = new();
        component.ShowSeenRoles(CloudRoles.From(Seen(("prod-go-web-eurw", "9"))));

        component.Roles = "prod-go-web-eurw, prod-go-typo-eurw";

        Assert.Equal(["prod-go-web-eurw", "prod-go-typo-eurw"], component.SeenRoles.Select(r => r.Name));
        Assert.All(component.SeenRoles, r => Assert.True(r.IsSelected));
    }

    [Fact]
    public void A_worker_serving_no_requests_says_what_it_did_send()
    {
        var roles = CloudRoles.From(new QueryTable(["cloud_RoleName", "requests", "other"], [["worker", "0", "1"], ["web", "1", "0"]]));

        Assert.Equal("no requests, ~1 dependency call or exception in the last day", new SelectableRole("worker", roles[0], false).Detail);
        Assert.Equal("~1 request in the last day", new SelectableRole("web", roles[1], false).Detail);
    }
}
