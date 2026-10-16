using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xrm.Sdk.Metadata;
using Vela.Cli;
using Xunit;
using static Vela.Cli.CliExitCodes;

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
    public void Intersect_is_rejected_with_the_typescript_scopes()
    {
        Assert.Equal(ExitInvalidArguments, ModelsCommand.Run(new[] { "--forms", "--intersect", "--entities", "account" }));
    }

    [Fact]
    public void Intersect_is_rejected_with_verify()
    {
        Assert.Equal(ExitInvalidArguments, ModelsCommand.Run(new[] { "--verify", "--intersect", "--entities", "account", "--namespace", "Zava.Models" }));
    }

    [Fact]
    public void CollectIntersectNames_discovers_dedupes_excludes_and_sorts()
    {
        // Discovery order is 'widgets' then 'apples' (reverse lexical), so a sorted result proves the sort.
        EntityMetadata account = Entity("account",
            ("widgets", "account", "widget"),
            ("apples", "account", "apple"),
            ("excludedjoin", "account", "task"));
        // 'apples' reachable from two selected tables -> emitted once.
        EntityMetadata contact = Entity("contact", ("apples", "contact", "apple"));

        // 'excludedjoin' is already in the batch (e.g. the user also selected it), so it is excluded.
        HashSet<string> have = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "account", "contact", "excludedjoin" };

        List<string> names = EntityMetadataReader.CollectIntersectNames(new[] { account, contact }, have);

        Assert.Equal(new[] { "apples", "widgets" }, names);
    }

    [Fact]
    public void AppendIntersects_reads_discovered_intersects_appends_them_and_counts()
    {
        List<EntityMetadata> metadata = new List<EntityMetadata> { Entity("account", ("accountleads", "account", "lead")) };

        int added = EntityMetadataReader.AppendIntersects(metadata, name => Entity(name), onSkip: _ => { });

        Assert.Equal(1, added);
        Assert.Contains(metadata, m => m.LogicalName == "accountleads");
    }

    [Fact]
    public void AppendIntersects_skips_a_failed_read_without_counting_it()
    {
        List<EntityMetadata> metadata = new List<EntityMetadata>
        {
            Entity("account", ("good", "account", "lead"), ("bad", "account", "task")),
        };
        List<string> skips = new List<string>();

        int added = EntityMetadataReader.AppendIntersects(
            metadata,
            name => name == "bad" ? throw new InvalidOperationException("no access") : Entity(name),
            skips.Add);

        Assert.Equal(1, added);
        Assert.Contains(metadata, m => m.LogicalName == "good");
        Assert.DoesNotContain(metadata, m => m.LogicalName == "bad");
        Assert.Single(skips);
    }

    // A network error is not a table the caller cannot read: it propagates, so the command exits 6 instead of
    // writing an incomplete model set.
    [Fact]
    public void AppendIntersects_does_not_skip_a_network_error()
    {
        List<EntityMetadata> metadata = new List<EntityMetadata>
        {
            Entity("account", ("good", "account", "lead"), ("bad", "account", "task")),
        };
        List<string> skips = new List<string>();

        Assert.Throws<System.Net.Http.HttpRequestException>(() => EntityMetadataReader.AppendIntersects(
            metadata,
            name => name == "bad" ? throw new System.Net.Http.HttpRequestException("Connection reset by peer") : Entity(name),
            skips.Add));
        Assert.Empty(skips);
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
