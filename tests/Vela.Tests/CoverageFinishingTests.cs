using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Vela.CustomApi;
using Vela.Manifest;
using Vela.StepBuilder;
using Xunit;

namespace Vela.Tests;

// Mops up small reachable branches: the AddRange extension (shadowed by List.AddRange elsewhere), the
// no-logical-name-attribute BoundTo fallbacks on both Custom API builders, a non-required typed parameter,
// and the FrameworkManifestScanner null guard.
public sealed class CoverageFinishingTests
{
    private sealed class Plain : Entity
    {
        public Plain() : base("plain") { }
    }

    private sealed class Req
    {
        [Required]
        public string Name { get; set; } = string.Empty;

        public string Note { get; set; } = string.Empty; // not required -> optional via the null-attribute branch
    }

    private sealed class Resp
    {
        public string Message { get; set; } = string.Empty;
    }

    [Fact]
    public void AddRange_extension_appends_and_guards_null()
    {
        // HashSet has no instance AddRange, so these calls bind to the extension method under test.
        HashSet<StepDefinition> set = new HashSet<StepDefinition>();
        Assert.Throws<ArgumentNullException>(() => set.AddRange(null!));                                   // definitions null
        Assert.Throws<ArgumentNullException>(() => ((ICollection<StepDefinition>)null!).AddRange(set));    // collection null

        set.AddRange(new[] { new StepDefinition(), new StepDefinition() });
        Assert.Equal(2, set.Count);
    }

    [Fact]
    public void Typed_builder_BoundTo_falls_back_to_the_type_name_and_handles_optional_params()
    {
        CustomApiDefinition bound = new CustomApiBuilder<Req, Resp>()
            .UniqueName("vela_Bound")
            .BoundTo<Plain>() // no [EntityLogicalName] -> "plain"
            .Request(b => b.Add(r => r.Name).Add(r => r.Note))
            .Build();
        Assert.Equal("plain", bound.BoundEntityLogicalName);
        Assert.False(bound.InputParameters.Single(p => p.Name == "Name").IsOptional); // [Required]
        Assert.True(bound.InputParameters.Single(p => p.Name == "Note").IsOptional);  // no [Required]

        CustomApiDefinition collection = new CustomApiBuilder<Req, Resp>()
            .UniqueName("vela_BoundColl")
            .BoundToCollection<Plain>()
            .Build();
        Assert.Equal(CustomApiBindingType.EntityCollection, collection.BindingType);
        Assert.Equal("plain", collection.BoundEntityLogicalName);
    }

    [Fact]
    public void Light_builder_BoundTo_falls_back_to_the_type_name()
    {
        CustomApiDefinition bound = new CustomApiLightBuilder().UniqueName("vela_LBound").BoundTo<Plain>().Build();
        Assert.Equal("plain", bound.BoundEntityLogicalName);

        CustomApiDefinition coll = new CustomApiLightBuilder().UniqueName("vela_LColl").BoundToCollection<Plain>().Build();
        Assert.Equal(CustomApiBindingType.EntityCollection, coll.BindingType);
        Assert.Equal("plain", coll.BoundEntityLogicalName);
    }

    [Fact]
    public void Manifest_scanner_rejects_a_null_assembly()
    {
        Assert.Throws<ArgumentNullException>(() => FrameworkManifestScanner.Scan(null!));
    }
}
