using System;
using System.Linq;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public class CustomApiMessageGeneratorTests
{
    private static string Generate(CustomApiMessage message) =>
        CustomApiMessageGenerator.Generate(new[] { message }, "Acme.Shared.Models.Generated").Code();

    [Fact]
    public void Bound_RequestParameterNamedTarget_DoesNotDuplicateTheAutoTargetMember()   // #308 M3
    {
        // A bound API auto-emits a 'Target' EntityReference property. A request parameter also named "Target" would
        // duplicate that member (CS0102); it must be skipped instead so the output compiles.
        string code = Generate(new CustomApiMessage(
            "zava_Recalc",
            IsBoundToEntity: true,
            RequestParameters: new[] { new CustomApiParameter("Target", 5), new CustomApiParameter("Amount", 8) },  // 5 EntityReference, 8 Money
            ResponseProperties: Array.Empty<CustomApiParameter>()));

        // Exactly one 'Target' member (the auto bound-record property); the duplicate parameter is skipped + documented.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(code, "EntityReference Target"));
        Assert.Contains("shares the 'Target' member emitted above", code);
        // The other, non-colliding parameter is still emitted.
        Assert.Contains("public Money Amount", code);
    }

    [Fact]
    public void Generates_TypedRequestAndResponse()
    {
        CustomApiMessage message = new CustomApiMessage(
            "zava_Recalculate",
            IsBoundToEntity: true,
            RequestParameters: new[] { new CustomApiParameter("AccountId", 7) },   // Integer
            ResponseProperties: new[] { new CustomApiParameter("Total", 2) });      // Decimal

        string code = Generate(message);

        Assert.Contains("public partial class zava_RecalculateRequest : Vela.Messages.CustomApiRequest", code);
        // The API unique name flows to the base as RequestName; the bags are keyed by the plain property name.
        Assert.Contains("public zava_RecalculateRequest() : base(\"zava_Recalculate\")", code);
        Assert.Contains("public EntityReference Target", code);   // bound → implicit Target
        Assert.Contains("public int AccountId", code);
        Assert.Contains("get => Get<int>();", code);
        Assert.Contains("set => Set(value);", code);

        Assert.Contains("public partial class zava_RecalculateResponse : Vela.Messages.CustomApiResponse", code);
        Assert.Contains("public zava_RecalculateResponse() : base(\"zava_Recalculate\")", code);
        Assert.DoesNotContain("From(OrganizationResponse", code);   // wrap via ToResponse<T>() instead
        Assert.Contains("public decimal Total => Get<decimal>();", code);
    }

    [Fact]
    public void RequiredRequestParameter_EmitsRequiredAttribute()
    {
        CustomApiMessage message = new CustomApiMessage(
            "zava_Score",
            IsBoundToEntity: false,
            RequestParameters: new[]
            {
                new CustomApiParameter("AccountId", 12),                       // Guid, required
                new CustomApiParameter("AsOfDate", 1, IsOptional: true),       // DateTime, optional
            },
            ResponseProperties: new[] { new CustomApiParameter("Total", 2) });

        string nl = Environment.NewLine;
        string code = Generate(message);

        // Required request parameter carries [Required] on the line directly above it; the optional one does not.
        Assert.Contains($"[System.ComponentModel.DataAnnotations.Required]{nl}        public Guid AccountId", code);
        Assert.DoesNotContain($"[System.ComponentModel.DataAnnotations.Required]{nl}        public DateTime AsOfDate", code);
    }

    // A parameter's table (logicalentityname) round-trips through [LogicalEntityName], which the typed builder reads.
    [Fact]
    public void Parameter_with_a_table_EmitsLogicalEntityNameAttribute()
    {
        CustomApiMessage message = new CustomApiMessage(
            "zava_Assign",
            IsBoundToEntity: false,
            RequestParameters: new[]
            {
                new CustomApiParameter("Owner", 5, LogicalEntityName: "systemuser"),   // EntityReference with a table
                new CustomApiParameter("Record", 5),                                  // EntityReference, any table
            },
            ResponseProperties: new[] { new CustomApiParameter("Created", 5, LogicalEntityName: "contact") });

        string code = Generate(message);
        string nl = Environment.NewLine;

        Assert.Contains($"[Vela.Messages.LogicalEntityName(\"systemuser\")]{nl}        [System.ComponentModel.DataAnnotations.Required]{nl}        public EntityReference Owner", code);
        Assert.Contains($"[Vela.Messages.LogicalEntityName(\"contact\")]{nl}        public EntityReference Created", code);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(code, "LogicalEntityName\\(").Count);
    }

    [Fact]
    public void ResponseProperty_NeverEmitsRequiredAttribute()
    {
        CustomApiMessage message = new CustomApiMessage(
            "zava_Score",
            IsBoundToEntity: false,
            RequestParameters: Array.Empty<CustomApiParameter>(),
            ResponseProperties: new[] { new CustomApiParameter("Total", 2) });

        string code = Generate(message);

        Assert.DoesNotContain("[System.ComponentModel.DataAnnotations.Required]", code);
    }

    [Fact]
    public void Unbound_HasNoTarget()
    {
        string code = Generate(new CustomApiMessage("zava_Ping", false, Array.Empty<CustomApiParameter>(), Array.Empty<CustomApiParameter>()));

        Assert.DoesNotContain("public EntityReference Target", code);
        Assert.Contains("public partial class zava_PingRequest : Vela.Messages.CustomApiRequest", code);
    }

    [Fact]
    public void FileName_IsUniqueName()
    {
        GeneratedModel model = CustomApiMessageGenerator
            .Generate(new[] { new CustomApiMessage("zava_X", false, Array.Empty<CustomApiParameter>(), Array.Empty<CustomApiParameter>()) }, "N")
            .First();

        Assert.Equal("Messages/zava_X.cs", model.FileName);
    }

    // ---- Phase 3c: registrability-gated On<Message>() step extension ----

    [Fact]
    public void Bound_SyncAndAsync_EmitsEntityConstrainedExtension_StrippingPublisherPrefix()
    {
        string code = Generate(new CustomApiMessage(
            "zava_AccountEstimateValue",
            IsBoundToEntity: true,
            RequestParameters: Array.Empty<CustomApiParameter>(),
            ResponseProperties: Array.Empty<CustomApiParameter>(),
            AllowedStepType: 2,
            BoundEntityLogicalName: "account"));

        Assert.Contains("internal static class zava_AccountEstimateValueStepExtensions", code);
        // Method name strips the publisher prefix; constrained to IStepTrigger<Account>; normal (Sync+Async) stage.
        Assert.Contains(
            "public static Vela.Steps.ITypedStages<Account, zava_AccountEstimateValueRequest> OnAccountEstimateValue(this Vela.Steps.IStepTrigger<Account> builder) => builder.On<zava_AccountEstimateValueRequest>();",
            code);
        Assert.DoesNotContain("Onzava_", code);
        Assert.DoesNotContain("IAsyncOnly", code);
    }

    [Fact]
    public void AsyncOnly_EmitsAsyncOnlyStageAndEntry()
    {
        string code = Generate(new CustomApiMessage(
            "zava_Recalc",
            IsBoundToEntity: true,
            RequestParameters: Array.Empty<CustomApiParameter>(),
            ResponseProperties: Array.Empty<CustomApiParameter>(),
            AllowedStepType: 1,   // AsyncOnly
            BoundEntityLogicalName: "account"));

        Assert.Contains(
            "public static Vela.Steps.IAsyncOnlyTypedStages<Account, zava_RecalcRequest> OnRecalc(this Vela.Steps.IStepTrigger<Account> builder) => builder.OnAsyncOnly<zava_RecalcRequest>();",
            code);
    }

    [Fact]
    public void Global_SyncAndAsync_EmitsGenericExtension()
    {
        string code = Generate(new CustomApiMessage(
            "zava_Ping",
            IsBoundToEntity: false,
            RequestParameters: Array.Empty<CustomApiParameter>(),
            ResponseProperties: Array.Empty<CustomApiParameter>(),
            AllowedStepType: 2,
            BoundEntityLogicalName: null));

        Assert.Contains(
            "public static Vela.Steps.ITypedStages<TEntity, zava_PingRequest> OnPing<TEntity>(this Vela.Steps.IStepTrigger<TEntity> builder) where TEntity : Microsoft.Xrm.Sdk.Entity => builder.On<zava_PingRequest>();",
            code);
    }

    [Fact]
    public void None_EmitsNoExtension_ButExplanatoryComment()
    {
        string code = Generate(new CustomApiMessage(
            "zava_Internal",
            IsBoundToEntity: false,
            RequestParameters: Array.Empty<CustomApiParameter>(),
            ResponseProperties: Array.Empty<CustomApiParameter>(),
            AllowedStepType: 0));   // None

        Assert.DoesNotContain("StepExtensions", code);
        Assert.Contains("No OnInternal() step extension", code);
        Assert.Contains("allows no processing step", code);
        Assert.Contains("Use On<zava_InternalRequest>() explicitly", code);
    }

    [Fact]
    public void Private_EmitsNoExtension_ButExplanatoryComment()
    {
        string code = Generate(new CustomApiMessage(
            "zava_Secret",
            IsBoundToEntity: false,
            RequestParameters: Array.Empty<CustomApiParameter>(),
            ResponseProperties: Array.Empty<CustomApiParameter>(),
            AllowedStepType: 2,
            IsPrivate: true));

        Assert.DoesNotContain("StepExtensions", code);
        Assert.Contains("is private", code);
    }

    [Fact]
    public void Bound_UsesSchemaNameTypeFromMap_NotNaivePascalCase()   // A12
    {
        var map = new System.Collections.Generic.Dictionary<string, string> { ["new_customentity"] = "new_CustomEntity" };
        string code = CustomApiMessageGenerator.Generate(
            new[]
            {
                new CustomApiMessage("zava_Recalc", IsBoundToEntity: true,
                    Array.Empty<CustomApiParameter>(), Array.Empty<CustomApiParameter>(),
                    AllowedStepType: 2, BoundEntityLogicalName: "new_customentity"),
            },
            "N",
            map).Code();

        Assert.Contains("IStepTrigger<new_CustomEntity>", code);   // exact generated model type
        Assert.DoesNotContain("NewCustomentity", code);            // not the naive PascalCase
    }

    [Fact]
    public void Bound_ToEntityNotInBatch_DegradesToGenericExtension_SoOutputCompiles()   // #308 H1
    {
        // The bound entity is not part of the generated set, so referencing its early-bound type would be CS0246.
        string code = CustomApiMessageGenerator.Generate(
            new[]
            {
                new CustomApiMessage("zava_Recalc", IsBoundToEntity: true,
                    Array.Empty<CustomApiParameter>(), Array.Empty<CustomApiParameter>(),
                    AllowedStepType: 2, BoundEntityLogicalName: "contact"),
            },
            "N",
            entityTypeNames: null,
            generatedEntityLogicalNames: new[] { "account" }).Code();

        Assert.Contains("OnRecalc<TEntity>(this Vela.Steps.IStepTrigger<TEntity> builder)", code);
        Assert.Contains("where TEntity : Microsoft.Xrm.Sdk.Entity", code);
        Assert.DoesNotContain("IStepTrigger<Contact>", code);   // no dangling early-bound reference
    }

    [Fact]
    public void Bound_ToEntityInBatch_KeepsTheEntityConstrainedExtension()   // #308 H1 (positive)
    {
        string code = CustomApiMessageGenerator.Generate(
            new[]
            {
                new CustomApiMessage("zava_Recalc", IsBoundToEntity: true,
                    Array.Empty<CustomApiParameter>(), Array.Empty<CustomApiParameter>(),
                    AllowedStepType: 2, BoundEntityLogicalName: "account"),
            },
            "N",
            entityTypeNames: null,
            generatedEntityLogicalNames: new[] { "account" }).Code();

        Assert.Contains("IStepTrigger<Account>", code);
        Assert.DoesNotContain("OnRecalc<TEntity>", code);
    }

    [Fact]
    public void CrossPublisher_SameSuffix_IsDisambiguatedWithFullName()   // A18
    {
        var models = CustomApiMessageGenerator.Generate(
            new[]
            {
                new CustomApiMessage("zava_Calc", false, Array.Empty<CustomApiParameter>(), Array.Empty<CustomApiParameter>()),
                new CustomApiMessage("acme_Calc", false, Array.Empty<CustomApiParameter>(), Array.Empty<CustomApiParameter>()),
            },
            "N");

        string all = string.Join("\n", models.Select(m => m.Code));
        Assert.Contains("OnZavaCalc<", all);
        Assert.Contains("OnAcmeCalc<", all);
        // The bare colliding name must not be emitted as a standalone method (would be CS0121).
        Assert.DoesNotContain(" OnCalc<", all);
        Assert.DoesNotContain(" OnCalc(", all);
    }

    [Fact]
    public void KeywordParameterName_IsEscaped_WithoutBreakingTheRuntimeKey()   // A17
    {
        string code = Generate(new CustomApiMessage(
            "zava_X",
            IsBoundToEntity: false,
            RequestParameters: new[] { new CustomApiParameter("object", 10) },   // C# keyword, String
            ResponseProperties: Array.Empty<CustomApiParameter>()));

        Assert.Contains("public string @object", code);   // escaped member
        // @object's CallerMemberName is "object", so no explicit key needed - stays keyless.
        Assert.Contains("get => Get<string>();", code);
    }
}
