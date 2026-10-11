using System;
using System.Linq;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public class SdkMessageGeneratorTests
{
    private static string Generate(SdkMessage message) =>
        SdkMessageGenerator.Generate(new[] { message }, "Acme.Shared.Models.Generated").Code();

    [Fact]
    public void Generates_TypedRequestAndResponse()
    {
        SdkMessage message = new SdkMessage(
            "QualifyLead",
            RequestFields: new[]
            {
                new SdkMessageField("LeadId", "EntityReference"),
                new SdkMessageField("CreateAccount", "bool"),
            },
            ResponseFields: new[]
            {
                new SdkMessageField("CreatedEntities", "EntityCollection"),
            });

        string code = Generate(message);

        Assert.Contains("public partial class QualifyLeadRequest : Vela.Messages.TypedRequest", code);
        Assert.Contains("RequestName = \"QualifyLead\";", code);
        Assert.Contains("public EntityReference LeadId", code);
        Assert.Contains("set => Set(value);", code);
        Assert.Contains("public bool CreateAccount", code);

        Assert.Contains("public partial class QualifyLeadResponse : Vela.Messages.TypedResponse", code);
        Assert.DoesNotContain("From(OrganizationResponse", code);   // wrap via ToResponse<T>() instead
        Assert.Contains("public EntityCollection CreatedEntities => Get<EntityCollection>();", code);
    }

    [Fact]
    public void Request_TheFrameworkTableRoutes_HasNoSdkMessageAttribute()
    {
        // CloseQuote runs on Close (verified live 2026-10-09); the framework's request table already routes it, so the
        // generated class mentions only its real name.
        string code = Generate(new SdkMessage("CloseQuote", Array.Empty<SdkMessageField>(), Array.Empty<SdkMessageField>())
        {
            MessageName = "Close",
            Entities = new[] { "quote" },
        });

        Assert.DoesNotContain("SdkMessage(", code);
        Assert.Contains("RequestName = \"CloseQuote\";", code);
        Assert.Contains("public static Vela.Steps.ITypedStages<Quote, CloseQuoteRequest> OnCloseQuote(this Vela.Steps.IStepTrigger<Quote> builder)", code);
    }

    [Fact]
    public void Request_OnAnUnknownDifferentSdkMessage_CarriesSdkMessageAttribute()
    {
        string code = Generate(new SdkMessage("acme_CloseThing", Array.Empty<SdkMessageField>(), Array.Empty<SdkMessageField>())
        {
            MessageName = "acme_Close",
        });

        Assert.Contains("[Vela.Messages.SdkMessage(\"acme_Close\")]\n    public partial class acme_CloseThingRequest : Vela.Messages.TypedRequest", code.Replace("\r\n", "\n"));
    }

    [Fact]
    public void StepExtension_GoesToItsOwnFile_OutOfTheSharedContract()
    {
        var models = SdkMessageGenerator.Generate(new[]
        {
            new SdkMessage("QualifyLead", Array.Empty<SdkMessageField>(), Array.Empty<SdkMessageField>()) { Entities = new[] { "lead" } },
        }, "Acme.Shared.Models.Generated");

        Assert.Equal(new[] { "Messages/QualifyLead.cs", "Messages/QualifyLead.Steps.cs" }, models.Select(m => m.FileName).ToArray());
        Assert.DoesNotContain("Vela.Steps", models[0].Code);
        Assert.Contains("internal static class QualifyLeadStepExtensions", models[1].Code);
    }

    [Fact]
    public void SdkBackedRequest_EmitsOnlyTheStepExtension_OverTheSdkType()
    {
        string code = Generate(new SdkMessage("CloseQuote", Array.Empty<SdkMessageField>(), Array.Empty<SdkMessageField>())
        {
            MessageName = "Close",
            Entities = new[] { "quote" },
            SdkRequestType = "Microsoft.Crm.Sdk.Messages.CloseQuoteRequest",
        });

        Assert.DoesNotContain("class CloseQuoteRequest", code);
        Assert.DoesNotContain("class CloseQuoteResponse", code);
        Assert.Contains("public static Vela.Steps.ITypedStages<Quote, Microsoft.Crm.Sdk.Messages.CloseQuoteRequest> OnCloseQuote(this Vela.Steps.IStepTrigger<Quote> builder) => builder.On<Microsoft.Crm.Sdk.Messages.CloseQuoteRequest>();", code);
    }

    [Fact]
    public void Request_OnItsOwnSdkMessage_HasNoSdkMessageAttribute()
    {
        string code = Generate(new SdkMessage("QualifyLead", Array.Empty<SdkMessageField>(), Array.Empty<SdkMessageField>()));

        Assert.DoesNotContain("SdkMessage(", code);
    }

    [Fact]
    public void RequestField_IsReadWrite_Over_Parameters()
    {
        string code = Generate(new SdkMessage(
            "DoThing",
            new[] { new SdkMessageField("Value", "int") },
            Array.Empty<SdkMessageField>()));

        Assert.Contains("get => Get<int>();", code);
        Assert.Contains("set => Set(value);", code);
    }

    [Fact]
    public void ResponseField_IsReadOnly_Over_Results()
    {
        string code = Generate(new SdkMessage(
            "DoThing",
            Array.Empty<SdkMessageField>(),
            new[] { new SdkMessageField("Total", "decimal") }));

        // Expression-bodied read-only property: no setter emitted for a response field.
        Assert.Contains("public decimal Total => Get<decimal>();", code);
        Assert.DoesNotContain("set => Set(value);", code);
    }

    [Fact]
    public void EmptyRequestAndResponse_StillEmitsBothClasses()
    {
        string code = Generate(new SdkMessage("WhoAmI", Array.Empty<SdkMessageField>(), Array.Empty<SdkMessageField>()));

        Assert.Contains("public partial class WhoAmIRequest : Vela.Messages.TypedRequest", code);
        Assert.Contains("RequestName = \"WhoAmI\";", code);
        Assert.Contains("public partial class WhoAmIResponse : Vela.Messages.TypedResponse", code);
        Assert.DoesNotContain("From(OrganizationResponse", code);
    }

    [Fact]
    public void FileName_IsMessageName_InMessagesFolder()
    {
        GeneratedModel model = SdkMessageGenerator
            .Generate(new[] { new SdkMessage("AddToQueue", Array.Empty<SdkMessageField>(), Array.Empty<SdkMessageField>()) }, "N")
            .First();

        Assert.Equal("Messages/AddToQueue.cs", model.FileName);
    }

    [Fact]
    public void Orders_Messages_ByName_Ordinal()
    {
        var models = SdkMessageGenerator.Generate(
            new[]
            {
                new SdkMessage("Zeta", Array.Empty<SdkMessageField>(), Array.Empty<SdkMessageField>()),
                new SdkMessage("Alpha", Array.Empty<SdkMessageField>(), Array.Empty<SdkMessageField>()),
            },
            "N");

        Assert.Equal(new[] { "Messages/Alpha.cs", "Messages/Alpha.Steps.cs", "Messages/Zeta.cs", "Messages/Zeta.Steps.cs" }, models.Select(m => m.FileName).ToArray());
    }

    [Fact]
    public void Skips_BlankNames()
    {
        var models = SdkMessageGenerator.Generate(
            new[]
            {
                new SdkMessage("  ", Array.Empty<SdkMessageField>(), Array.Empty<SdkMessageField>()),
                new SdkMessage("Real", Array.Empty<SdkMessageField>(), Array.Empty<SdkMessageField>()),
            },
            "N");

        Assert.Equal(new[] { "Messages/Real.cs", "Messages/Real.Steps.cs" }, models.Select(m => m.FileName).ToArray());
    }

    [Fact]
    public void SingleEntity_EmitsEntityConstrainedOnExtension()
    {
        string code = Generate(new SdkMessage(
            "QualifyLead",
            Array.Empty<SdkMessageField>(),
            Array.Empty<SdkMessageField>())
        {
            Entities = new[] { "lead" },
        });

        Assert.Contains("internal static class QualifyLeadStepExtensions", code);
        Assert.Contains(
            "public static Vela.Steps.ITypedStages<Lead, QualifyLeadRequest> OnQualifyLead(this Vela.Steps.IStepTrigger<Lead> builder) => builder.On<QualifyLeadRequest>();",
            code);
        // A concrete single-entity message does not emit the generic <TEntity> form.
        Assert.DoesNotContain("OnQualifyLead<TEntity>", code);
    }

    [Fact]
    public void TwoEntities_EmitsTwoOverloads_PascalCased()
    {
        string code = Generate(new SdkMessage(
            "Merge",
            Array.Empty<SdkMessageField>(),
            Array.Empty<SdkMessageField>())
        {
            Entities = new[] { "account", "contact" },
        });

        Assert.Contains(
            "public static Vela.Steps.ITypedStages<Account, MergeRequest> OnMerge(this Vela.Steps.IStepTrigger<Account> builder) => builder.On<MergeRequest>();",
            code);
        Assert.Contains(
            "public static Vela.Steps.ITypedStages<Contact, MergeRequest> OnMerge(this Vela.Steps.IStepTrigger<Contact> builder) => builder.On<MergeRequest>();",
            code);
    }

    [Fact]
    public void UnderscoredLogicalName_IsPascalCased()
    {
        string code = Generate(new SdkMessage(
            "DoThing",
            Array.Empty<SdkMessageField>(),
            Array.Empty<SdkMessageField>())
        {
            Entities = new[] { "new_customentity" },
        });

        Assert.Contains(
            "ITypedStages<NewCustomentity, DoThingRequest> OnDoThing(this Vela.Steps.IStepTrigger<NewCustomentity> builder)",
            code);
    }

    [Fact]
    public void Entities_NotInBatch_AreDropped_SoOutputCompiles()   // #308 H1
    {
        string code = SdkMessageGenerator.Generate(
            new[]
            {
                new SdkMessage("Merge", Array.Empty<SdkMessageField>(), Array.Empty<SdkMessageField>())
                {
                    Entities = new[] { "account", "contact" },
                },
            },
            "N",
            entityTypeNames: null,
            generatedEntityLogicalNames: new[] { "account" }).Code();

        Assert.Contains("IStepTrigger<Account>", code);       // the generated entity keeps its overload
        Assert.DoesNotContain("IStepTrigger<Contact>", code); // the absent one is dropped (no dangling reference)
    }

    [Fact]
    public void AllEntities_OutOfBatch_FallsBackToGenericExtension()   // #308 H1
    {
        string code = SdkMessageGenerator.Generate(
            new[]
            {
                new SdkMessage("Merge", Array.Empty<SdkMessageField>(), Array.Empty<SdkMessageField>())
                {
                    Entities = new[] { "account", "contact" },
                },
            },
            "N",
            entityTypeNames: null,
            generatedEntityLogicalNames: Array.Empty<string>()).Code();

        Assert.Contains("OnMerge<TEntity>(this Vela.Steps.IStepTrigger<TEntity> builder) where TEntity : Microsoft.Xrm.Sdk.Entity", code);
        Assert.DoesNotContain("IStepTrigger<Account>", code);
        Assert.DoesNotContain("IStepTrigger<Contact>", code);
    }

    [Fact]
    public void NoEntities_EmitsGenericExtension()
    {
        string code = Generate(new SdkMessage(
            "Create",
            Array.Empty<SdkMessageField>(),
            Array.Empty<SdkMessageField>()));   // Entities defaults to empty -> all-entity message.

        Assert.Contains(
            "public static Vela.Steps.ITypedStages<TEntity, CreateRequest> OnCreate<TEntity>(this Vela.Steps.IStepTrigger<TEntity> builder) where TEntity : Microsoft.Xrm.Sdk.Entity => builder.On<CreateRequest>();",
            code);
    }

    [Fact]
    public void MoreThanEightEntities_FallsBackToGenericExtension()
    {
        string[] many = Enumerable.Range(0, 9).Select(i => "entity" + i).ToArray();

        string code = Generate(new SdkMessage(
            "Update",
            Array.Empty<SdkMessageField>(),
            Array.Empty<SdkMessageField>())
        {
            Entities = many,
        });

        Assert.Contains("OnUpdate<TEntity>(this Vela.Steps.IStepTrigger<TEntity> builder) where TEntity : Microsoft.Xrm.Sdk.Entity", code);
        // No per-entity overload was emitted.
        Assert.DoesNotContain("IStepTrigger<Entity0>", code);
    }

    [Fact]
    public void ExactlyEightEntities_EmitsPerEntityOverloads()
    {
        string[] eight = Enumerable.Range(0, 8).Select(i => "entity" + i).ToArray();

        string code = Generate(new SdkMessage(
            "Assign",
            Array.Empty<SdkMessageField>(),
            Array.Empty<SdkMessageField>())
        {
            Entities = eight,
        });

        Assert.Contains("ITypedStages<Entity0, AssignRequest> OnAssign(this Vela.Steps.IStepTrigger<Entity0> builder)", code);
        Assert.DoesNotContain("OnAssign<TEntity>", code);
    }

    [Fact]
    public void Generate_NullMessages_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SdkMessageGenerator.Generate(null!, "N"));
    }

    [Fact]
    public void Generate_BlankNamespace_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            SdkMessageGenerator.Generate(new[] { new SdkMessage("X", Array.Empty<SdkMessageField>(), Array.Empty<SdkMessageField>()) }, "  "));
    }
}

internal static class GeneratedCode
{
    // A message's contract and its .Steps.cs extension file, read together.
    public static string Code(this System.Collections.Generic.IEnumerable<GeneratedModel> models) => string.Join("\n", models.Select(m => m.Code));
}
