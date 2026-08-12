using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk.Metadata;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public class RolePrivilegeMapperTests
{
    [Theory]
    [InlineData(1, "User")]           // Basic
    [InlineData(2, "BusinessUnit")]   // Local
    [InlineData(4, "ParentChild")]    // Deep
    [InlineData(8, "Organization")]   // Global
    [InlineData(0, null)]             // no access
    [InlineData(1 | 8, "Organization")] // combined -> highest bit wins
    [InlineData(2 | 4, "ParentChild")]
    public void AccessLevel_maps_the_depth_mask_taking_the_highest_bit(int mask, string? expected)
    {
        Assert.Equal(expected, RolePrivilegeMapper.AccessLevel(mask));
    }

    [Theory]
    [InlineData(PrivilegeType.Create, "Create")]
    [InlineData(PrivilegeType.Read, "Read")]
    [InlineData(PrivilegeType.Write, "Write")]
    [InlineData(PrivilegeType.Delete, "Delete")]
    [InlineData(PrivilegeType.Append, "Append")]
    [InlineData(PrivilegeType.AppendTo, "AppendTo")]
    [InlineData(PrivilegeType.Assign, "Assign")]
    [InlineData(PrivilegeType.Share, "Share")]
    [InlineData(PrivilegeType.None, null)]
    public void Verb_maps_entity_privilege_types_only(PrivilegeType type, string? expected)
    {
        Assert.Equal(expected, RolePrivilegeMapper.Verb(type));
    }

    [Fact]
    public void ToGrants_resolves_privileges_and_skips_the_unmapped_and_non_entity()
    {
        Guid read = Guid.NewGuid();
        Guid write = Guid.NewGuid();
        Guid misc = Guid.NewGuid();        // e.g. "Export to Excel" — not in any table's privileges
        Guid none = Guid.NewGuid();        // an entity privilege of type None

        var privileges = new Dictionary<Guid, (string Entity, PrivilegeType Type)>
        {
            [read] = ("account", PrivilegeType.Read),
            [write] = ("account", PrivilegeType.Write),
            [none] = ("account", PrivilegeType.None),
        };

        var rolePrivileges = new[]
        {
            (read, 2),      // Read @ BusinessUnit
            (write, 8),     // Write @ Organization
            (none, 8),      // skipped: type None
            (misc, 8),      // skipped: not resolvable to a table
        };

        IReadOnlyList<RoleGrantModel> grants = RolePrivilegeMapper.ToGrants(rolePrivileges, privileges);

        Assert.Equal(2, grants.Count);
        Assert.Contains(grants, g => g.Entity == "account" && g.Privilege == "Read" && g.AccessLevel == "BusinessUnit");
        Assert.Contains(grants, g => g.Entity == "account" && g.Privilege == "Write" && g.AccessLevel == "Organization");
    }

    [Fact]
    public void ToGrants_keeps_the_highest_access_level_for_a_repeated_table_verb()
    {
        Guid p1 = Guid.NewGuid();
        Guid p2 = Guid.NewGuid();
        var privileges = new Dictionary<Guid, (string, PrivilegeType)>
        {
            [p1] = ("account", PrivilegeType.Read),
            [p2] = ("account", PrivilegeType.Read),
        };

        IReadOnlyList<RoleGrantModel> grants = RolePrivilegeMapper.ToGrants(
            new[] { (p1, 1 /* User */), (p2, 4 /* ParentChild */) }, privileges);

        RoleGrantModel grant = Assert.Single(grants);
        Assert.Equal("ParentChild", grant.AccessLevel);
    }

    [Fact]
    public void ToGrants_orders_grants_by_entity_then_verb()
    {
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();
        var privileges = new Dictionary<Guid, (string, PrivilegeType)>
        {
            [a] = ("contact", PrivilegeType.Read),
            [b] = ("account", PrivilegeType.Write),
        };

        var grants = RolePrivilegeMapper.ToGrants(new[] { (a, 8), (b, 8) }, privileges).ToList();

        Assert.Equal("account", grants[0].Entity);
        Assert.Equal("contact", grants[1].Entity);
    }
}
