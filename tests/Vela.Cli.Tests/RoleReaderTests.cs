using System;
using System.Collections.Generic;
using System.Linq;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// <see cref="RoleReader"/> against an in-memory org: the roles of a solution (solutioncomponent componenttype 20),
/// merged with explicit names, each with its real root id and its privileges mapped to every table they cover.
/// </summary>
public sealed class RoleReaderTests
{
    [Fact]
    public void Solution_roles_are_read_with_their_root_id_and_privileges()
    {
        RoleOrg org = new();
        Guid solution = org.AddSolution("ZavaCore");
        Guid sales = org.AddRole("Sales Rep", (RoleOrg.PrvReadAccount, 8), (RoleOrg.PrvReadContact, 1));
        Guid other = org.AddRole("Not In Solution", (RoleOrg.PrvReadContact, 8));
        org.AddComponent(solution, sales, componentType: 20);
        org.AddComponent(solution, Guid.NewGuid(), componentType: 1); // a table component, not a role

        IReadOnlyList<RoleModel> roles = RoleReader.ReadRoles(org, [], "ZavaCore");

        RoleModel role = Assert.Single(roles);
        Assert.Equal("Sales Rep", role.Name);
        Assert.Equal(sales, role.Id);
        Assert.DoesNotContain(roles, r => r.Id == other);
        // prvReadAccount covers account and customeraddress; a depth mask of 8 is Organization, 1 is User.
        Assert.Equal(
            new[] { ("account", "Organization", "Read"), ("contact", "User", "Read"), ("customeraddress", "Organization", "Read") },
            role.Grants.Select(g => (g.Entity, g.AccessLevel, g.Privilege)).OrderBy(g => g.Entity, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void A_role_named_explicitly_and_present_in_the_solution_is_generated_once()
    {
        RoleOrg org = new();
        Guid solution = org.AddSolution("ZavaCore");
        Guid sales = org.AddRole("Sales Rep", (RoleOrg.PrvReadContact, 1));
        org.AddComponent(solution, sales, componentType: 20);

        IReadOnlyList<RoleModel> roles = RoleReader.ReadRoles(org, ["Sales Rep"], "ZavaCore");

        Assert.Equal(sales, Assert.Single(roles).Id);
    }

    [Fact]
    public void Explicit_roles_and_solution_roles_are_merged_and_sorted_by_name()
    {
        RoleOrg org = new();
        Guid solution = org.AddSolution("ZavaCore");
        Guid zed = org.AddRole("Zed", (RoleOrg.PrvReadContact, 1));
        org.AddRole("Alpha", (RoleOrg.PrvReadAccount, 1));
        org.AddComponent(solution, zed, componentType: 20);

        IReadOnlyList<RoleModel> roles = RoleReader.ReadRoles(org, ["Alpha"], "ZavaCore");

        Assert.Equal(new[] { "Alpha", "Zed" }, roles.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void A_privilege_no_table_carries_is_left_out()
    {
        RoleOrg org = new();
        Guid solution = org.AddSolution("ZavaCore");
        Guid sales = org.AddRole("Sales Rep", (RoleOrg.PrvExportToExcel, 8), (RoleOrg.PrvReadContact, 1));
        org.AddComponent(solution, sales, componentType: 20);

        RoleModel role = Assert.Single(RoleReader.ReadRoles(org, [], "ZavaCore"));

        Assert.Equal("contact", Assert.Single(role.Grants).Entity);
    }

    [Fact]
    public void An_unknown_solution_fails_with_its_name()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => RoleReader.ReadRoles(new RoleOrg(), [], "Missing"));

        Assert.Contains("'Missing'", error.Message);
    }

    [Fact]
    public void A_solution_without_roles_and_no_explicit_role_is_an_error()
    {
        RoleOrg org = new();
        org.AddSolution("Empty");

        Assert.Throws<InvalidOperationException>(() => RoleReader.ReadRoles(org, [], "Empty"));
    }
}
