using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xrm.Sdk.Metadata;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public sealed class IntersectModelsTests
{
    [Fact]
    public void Parse_reads_the_intersect_flag()
    {
        Assert.True(ModelsScope.Parse(new[] { "--entities", "account", "--intersect" }).Intersect);
        Assert.False(ModelsScope.Parse(new[] { "--entities", "account" }).Intersect);
    }

    [Fact]
    public void CollectIntersectNames_discovers_dedupes_excludes_and_sorts()
    {
        EntityMetadata account = Entity("account",
            ("accountleads", "account", "lead"),
            ("accounttasks", "account", "task"));
        // Same intersect reachable from two selected tables -> emitted once.
        EntityMetadata contact = Entity("contact", ("accountleads", "account", "lead"));

        // 'accounttasks' is already in the batch (e.g. the user also selected it), so it is excluded.
        HashSet<string> have = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "account", "contact", "accounttasks" };

        List<string> names = EntityMetadataReader.CollectIntersectNames(new[] { account, contact }, have);

        Assert.Equal(new[] { "accountleads" }, names);
    }

    [Fact]
    public void CollectIntersectNames_returns_empty_when_there_are_no_many_to_many()
    {
        List<string> names = EntityMetadataReader.CollectIntersectNames(
            new[] { Entity("account") }, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Empty(names);
    }

    // Builds EntityMetadata with N:N relationships through the SDK's read-only metadata setters (reflection), the
    // same dance the test harness uses - so no dependency on the plugin test library is needed here.
    private static EntityMetadata Entity(string logicalName, params (string Intersect, string End1, string End2)[] manyToMany)
    {
        EntityMetadata metadata = new EntityMetadata();
        SetSealed(metadata, nameof(EntityMetadata.LogicalName), logicalName);
        if (manyToMany.Length > 0)
        {
            ManyToManyRelationshipMetadata[] relationships = manyToMany
                .Select(m => new ManyToManyRelationshipMetadata
                {
                    IntersectEntityName = m.Intersect,
                    Entity1LogicalName = m.End1,
                    Entity2LogicalName = m.End2,
                })
                .ToArray();
            SetSealed(metadata, nameof(EntityMetadata.ManyToManyRelationships), relationships);
        }

        return metadata;
    }

    private static void SetSealed(object target, string propertyName, object? value)
    {
        PropertyInfo property = target.GetType().GetProperty(
            propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        property.GetSetMethod(nonPublic: true)!.Invoke(target, new[] { value });
    }
}
