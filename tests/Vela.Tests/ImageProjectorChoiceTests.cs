using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Microsoft.Xrm.Sdk.Messages;
using Vela.Constants;
using Vela.Context;
using Vela.Plugin;
using Vela.Steps;
using Vela.Testing;
using Xunit;

namespace Vela.Tests
{
    // Choice enums and an early-bound model shaped exactly as `vela models` (and pac modelbuilder) generate them:
    // State/Status/Picklist columns are nullable enums over an OptionSetValue, a multi-select column is a list of
    // enum values over an OptionSetValueCollection. The image Entity still holds the SDK types.
    public enum choice_account_statecode
    {
        Active = 0,
        Inactive = 1,
    }

    public enum choice_account_statuscode
    {
        Active = 1,
        Inactive = 2,
        OnHold = 100000000,
    }

    public enum choice_account_industrycode
    {
        Accounting = 1,
        Agriculture = 2,
    }

    public enum choice_account_vela_tags
    {
        Red = 100000000,
        Blue = 100000001,
    }

    [EntityLogicalName(EntityLogicalNameValue)]
    public sealed class ChoiceAccount : Entity
    {
        public const string EntityLogicalNameValue = "account";

        public ChoiceAccount()
            : base(EntityLogicalNameValue)
        {
        }

        [AttributeLogicalName("name")]
        public string Name
        {
            get => GetAttributeValue<string>("name");
            set => SetAttributeValue("name", value);
        }

        [AttributeLogicalName("statecode")]
        public choice_account_statecode? StateCode
        {
            get { OptionSetValue raw = GetAttributeValue<OptionSetValue>("statecode"); return raw == null ? null : (choice_account_statecode)raw.Value; }
            set => SetAttributeValue("statecode", value.HasValue ? new OptionSetValue((int)value.Value) : null);
        }

        [AttributeLogicalName("statuscode")]
        public choice_account_statuscode? StatusCode
        {
            get { OptionSetValue raw = GetAttributeValue<OptionSetValue>("statuscode"); return raw == null ? null : (choice_account_statuscode)raw.Value; }
            set => SetAttributeValue("statuscode", value.HasValue ? new OptionSetValue((int)value.Value) : null);
        }

        [AttributeLogicalName("industrycode")]
        public choice_account_industrycode? IndustryCode
        {
            get { OptionSetValue raw = GetAttributeValue<OptionSetValue>("industrycode"); return raw == null ? null : (choice_account_industrycode)raw.Value; }
            set => SetAttributeValue("industrycode", value.HasValue ? new OptionSetValue((int)value.Value) : null);
        }

        [AttributeLogicalName("vela_tags")]
        public IList<choice_account_vela_tags> Tags
        {
            get
            {
                OptionSetValueCollection raw = GetAttributeValue<OptionSetValueCollection>("vela_tags");
                if (raw == null) { return null!; }
                List<choice_account_vela_tags> values = new List<choice_account_vela_tags>();
                foreach (OptionSetValue option in raw) { values.Add((choice_account_vela_tags)option.Value); }
                return values;
            }
            set
            {
                if (value == null) { SetAttributeValue("vela_tags", null); return; }
                OptionSetValueCollection raw = new OptionSetValueCollection();
                foreach (choice_account_vela_tags option in value) { raw.Add(new OptionSetValue((int)option)); }
                SetAttributeValue("vela_tags", raw);
            }
        }
    }

    // Stand-in for the projection the build-time generator emits for
    // WithPreImage(a => a.StateCode, a => a.StatusCode, a => a.IndustryCode, a => a.Tags): each property has the
    // early-bound column's type, plus the IImageProjection source and the explicit widening cast.
    public sealed class ChoicePreImage : IImageProjection
    {
        [AttributeLogicalName("statecode")]
        public choice_account_statecode? StateCode { get; set; }

        [AttributeLogicalName("statuscode")]
        public choice_account_statuscode? StatusCode { get; set; }

        [AttributeLogicalName("industrycode")]
        public choice_account_industrycode? IndustryCode { get; set; }

        [AttributeLogicalName("vela_tags")]
        public IList<choice_account_vela_tags> Tags { get; set; } = null!;

        Entity? IImageProjection.Source { get; set; }

        public static explicit operator ChoiceAccount(ChoicePreImage? projection)
            => projection is IImageProjection { Source: { } source } ? source.ToEntity<ChoiceAccount>() : null!;
    }

    public sealed class ChoiceImagePlugin : EntityPluginBase<ChoiceAccount>
    {
        public static ChoicePreImage? Seen;
        public static ChoiceAccount? Widened;

        public ChoiceImagePlugin(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdate().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .WithPreImage(a => a.StateCode, a => a.StatusCode, a => a.IndustryCode, a => a.Tags)
                .Handle(OnUpdated);

        private void OnUpdated(IUpdatePostContext<ChoiceAccount> ctx)
        {
            Seen = ctx.PreImageAs<ChoicePreImage>();
            Widened = (ChoiceAccount)Seen!;
        }
    }

    public sealed class ChoiceBulkImagePlugin : EntityPluginBase<ChoiceAccount>
    {
        public static IReadOnlyList<ChoicePreImage?>? Seen;

        public ChoiceBulkImagePlugin(string unsecure, string secure) : base(unsecure, secure) { }

        protected override void RegisterPluginSteps()
            => RegisterStep().OnUpdateMultiple().PostOperation().Sync()
                .FilterOn(a => a.Name)
                .WithPreImage(a => a.StateCode, a => a.StatusCode, a => a.IndustryCode, a => a.Tags)
                .Handle(OnUpdatedMultiple);

        private void OnUpdatedMultiple(IUpdateMultiplePostContext<ChoiceAccount> ctx)
            => Seen = ctx.PreImagesAs<ChoicePreImage>();
    }

    public sealed class ImageProjectorChoiceTests
    {
        // Projection shapes a hand-written or differently generated model may use for the same choice columns.
        private sealed class AlternativeShapes
        {
            [AttributeLogicalName("statecode")]
            public choice_account_statecode State { get; set; } = (choice_account_statecode)(-1);

            [AttributeLogicalName("statuscode")]
            public int? StatusValue { get; set; }

            [AttributeLogicalName("industrycode")]
            public int IndustryValue { get; set; } = -1;

            [AttributeLogicalName("tags_enumerable")]
            public IEnumerable<choice_account_vela_tags>? AsEnumerable { get; set; }

            [AttributeLogicalName("tags_readonly")]
            public IReadOnlyList<choice_account_vela_tags>? AsReadOnlyList { get; set; }

            [AttributeLogicalName("tags_list")]
            public List<choice_account_vela_tags>? AsList { get; set; }

            [AttributeLogicalName("tags_array")]
            public choice_account_vela_tags[]? AsArray { get; set; }
        }

        // Shapes a choice value can not be converted to: still skipped and traced.
        private sealed class UnconvertibleShapes
        {
            [AttributeLogicalName("statecode")]
            public string? StateText { get; set; }

            [AttributeLogicalName("statuscode")]
            public choice_account_statuscode? Status { get; set; }

            [AttributeLogicalName("vela_tags")]
            public HashSet<choice_account_vela_tags>? TagSet { get; set; }

            [AttributeLogicalName("tags_as_enum")]
            public choice_account_vela_tags? SingleTag { get; set; }

            [AttributeLogicalName("industrycode")]
            public IList<choice_account_vela_tags>? IndustryAsList { get; set; }

            [AttributeLogicalName("tags_as_int")]
            public int? TagsAsInt { get; set; }

            [AttributeLogicalName("tags_as_ints")]
            public List<int>? TagsAsInts { get; set; }
        }

        // Joined names: xunit's collection comparer mis-reads enum arrays under mono, so compare the text.
        private static string Seq(IEnumerable<choice_account_vela_tags>? values)
            => values == null ? "(null)" : string.Join(",", values);

        private static OptionSetValueCollection Tags(params int[] values)
            => new OptionSetValueCollection(values.Select(v => new OptionSetValue(v)).ToList());

        [Fact]
        public void Pipeline_pre_image_projects_every_choice_column_to_its_enum()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<ChoiceImagePlugin>()
                .Seed(new ChoiceAccount
                {
                    Id = id,
                    Name = "Old",
                    StateCode = choice_account_statecode.Inactive,
                    StatusCode = choice_account_statuscode.OnHold,
                    IndustryCode = choice_account_industrycode.Agriculture,
                    Tags = new[] { choice_account_vela_tags.Blue, choice_account_vela_tags.Red },
                });
            ChoiceImagePlugin.Seen = null;

            pipeline.Service.Update(new ChoiceAccount { Id = id, Name = "New" });

            ChoicePreImage pre = Assert.IsType<ChoicePreImage>(ChoiceImagePlugin.Seen);
            Assert.Equal(choice_account_statecode.Inactive, pre.StateCode);
            Assert.Equal(choice_account_statuscode.OnHold, pre.StatusCode);
            Assert.Equal(choice_account_industrycode.Agriculture, pre.IndustryCode);
            Assert.Equal("Blue,Red", Seq(pre.Tags));
        }

        [Fact]
        public void Explicit_cast_reads_the_choices_through_the_early_bound_getters()
        {
            Guid id = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<ChoiceImagePlugin>()
                .Seed(new ChoiceAccount
                {
                    Id = id,
                    Name = "Old",
                    StateCode = choice_account_statecode.Active,
                    IndustryCode = choice_account_industrycode.Accounting,
                    Tags = new[] { choice_account_vela_tags.Red },
                });
            ChoiceImagePlugin.Widened = null;

            pipeline.Service.Update(new ChoiceAccount { Id = id, Name = "New" });

            ChoiceAccount full = Assert.IsType<ChoiceAccount>(ChoiceImagePlugin.Widened);
            Assert.Equal(id, full.Id);
            Assert.Equal(choice_account_statecode.Active, full.StateCode);
            Assert.Equal(choice_account_industrycode.Accounting, full.IndustryCode);
            Assert.Equal("Red", Seq(full.Tags));
        }

        [Fact]
        public void PluginContext_pre_image_leaves_absent_choice_columns_null()
        {
            Guid id = Guid.NewGuid();
            ChoiceImagePlugin.Seen = null;

            PluginContext.For<ChoiceAccount>()
                .OnUpdate().Stage(PluginStages.PostOperation).Sync()
                .WithTarget(new ChoiceAccount { Id = id, Name = "New" })
                .WithPreImage(new ChoiceAccount { Id = id, StatusCode = choice_account_statuscode.Active })
                .Execute(new ChoiceImagePlugin(null!, null!));

            ChoicePreImage pre = Assert.IsType<ChoicePreImage>(ChoiceImagePlugin.Seen);
            Assert.Equal(choice_account_statuscode.Active, pre.StatusCode);
            Assert.Null(pre.StateCode);
            Assert.Null(pre.IndustryCode);
            Assert.Null(pre.Tags);
        }

        [Fact]
        public void PluginContext_pre_image_with_a_wrong_runtime_type_skips_and_traces_it()
        {
            Guid id = Guid.NewGuid();
            ChoiceImagePlugin.Seen = null;
            Entity image = new Entity(ChoiceAccount.EntityLogicalNameValue, id)
            {
                ["statecode"] = "Active",
                ["statuscode"] = new OptionSetValue(2),
            };

            PluginContextBuilder<ChoiceAccount> builder = PluginContext.For<ChoiceAccount>()
                .OnUpdate().Stage(PluginStages.PostOperation).Sync()
                .WithTarget(new ChoiceAccount { Id = id, Name = "New" })
                .WithPreImage(image.ToEntity<ChoiceAccount>());
            builder.Execute(new ChoiceImagePlugin(null!, null!));

            ChoicePreImage pre = Assert.IsType<ChoicePreImage>(ChoiceImagePlugin.Seen);
            Assert.Null(pre.StateCode);
            Assert.Equal(choice_account_statuscode.Inactive, pre.StatusCode);
            Assert.Contains("ImageProjector: attribute 'statecode' skipped - expected "
                + typeof(choice_account_statecode?).FullName + " but the image value was System.String.", builder.TraceCapture.Output);
            Assert.DoesNotContain("'statuscode' skipped", builder.TraceCapture.Output);
        }

        [Fact]
        public void Bulk_pre_images_project_choice_columns_per_record()
        {
            Guid a = Guid.NewGuid();
            Guid b = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty().Register<ChoiceBulkImagePlugin>()
                .Seed(
                    new ChoiceAccount
                    {
                        Id = a,
                        Name = "A",
                        StateCode = choice_account_statecode.Inactive,
                        StatusCode = choice_account_statuscode.Inactive,
                        Tags = new[] { choice_account_vela_tags.Red },
                    },
                    new ChoiceAccount { Id = b, Name = "B", IndustryCode = choice_account_industrycode.Accounting });
            ChoiceBulkImagePlugin.Seen = null;

            EntityCollection targets = new EntityCollection { EntityName = ChoiceAccount.EntityLogicalNameValue };
            targets.Entities.Add(new ChoiceAccount { Id = a, Name = "A2" });
            targets.Entities.Add(new ChoiceAccount { Id = b, Name = "B2" });
            pipeline.Service.Execute(new UpdateMultipleRequest { Targets = targets });

            IReadOnlyList<ChoicePreImage?> pre = Assert.IsAssignableFrom<IReadOnlyList<ChoicePreImage?>>(ChoiceBulkImagePlugin.Seen);
            Assert.Equal(2, pre.Count);
            Assert.Equal(choice_account_statecode.Inactive, pre[0]!.StateCode);
            Assert.Equal(choice_account_statuscode.Inactive, pre[0]!.StatusCode);
            Assert.Null(pre[0]!.IndustryCode);
            Assert.Equal("Red", Seq(pre[0]!.Tags));
            Assert.Equal(choice_account_industrycode.Accounting, pre[1]!.IndustryCode);
            Assert.Null(pre[1]!.Tags);
        }

        [Fact]
        public void Project_converts_choices_to_non_nullable_enum_int_and_every_supported_collection_shape()
        {
            Entity image = new Entity("account")
            {
                ["statecode"] = new OptionSetValue(1),
                ["statuscode"] = new OptionSetValue(100000000),
                ["industrycode"] = new OptionSetValue(2),
                ["tags_enumerable"] = Tags(100000000, 100000001),
                ["tags_readonly"] = Tags(100000001),
                ["tags_list"] = Tags(100000000),
                ["tags_array"] = Tags(100000001, 100000000),
            };

            AlternativeShapes projection = ImageProjector.Project<AlternativeShapes>(image)!;

            Assert.Equal(choice_account_statecode.Inactive, projection.State);
            Assert.Equal(100000000, projection.StatusValue);
            Assert.Equal(2, projection.IndustryValue);
            Assert.Equal("Red,Blue", Seq(projection.AsEnumerable));
            Assert.Equal("Blue", Seq(projection.AsReadOnlyList));
            Assert.Equal("Red", Seq(projection.AsList));
            Assert.Equal("Blue,Red", Seq(projection.AsArray));
        }

        [Fact]
        public void Project_maps_an_empty_multi_select_to_an_empty_collection()
        {
            Entity image = new Entity("account") { ["tags_list"] = new OptionSetValueCollection(), ["tags_array"] = new OptionSetValueCollection() };

            AlternativeShapes projection = ImageProjector.Project<AlternativeShapes>(image)!;

            Assert.NotNull(projection.AsList);
            Assert.Empty(projection.AsList!);
            Assert.NotNull(projection.AsArray);
            Assert.Empty(projection.AsArray!);
        }

        [Fact]
        public void Project_returns_fresh_values_on_every_call()
        {
            // The per-property converter is cached; the converted values must not be.
            Entity first = new Entity("account") { ["tags_list"] = Tags(100000000), ["statecode"] = new OptionSetValue(0) };
            Entity second = new Entity("account") { ["tags_list"] = Tags(100000001), ["statecode"] = new OptionSetValue(1) };

            AlternativeShapes one = ImageProjector.Project<AlternativeShapes>(first)!;
            AlternativeShapes two = ImageProjector.Project<AlternativeShapes>(second)!;

            Assert.Equal("Red", Seq(one.AsList));
            Assert.Equal("Blue", Seq(two.AsList));
            Assert.NotSame(one.AsList, two.AsList);
            Assert.Equal(choice_account_statecode.Active, one.State);
            Assert.Equal(choice_account_statecode.Inactive, two.State);
        }

        [Fact]
        public void Project_skips_and_traces_choice_values_it_cannot_convert()
        {
            Entity image = new Entity("account")
            {
                ["statecode"] = new OptionSetValue(0),
                ["statuscode"] = Tags(1),
                ["vela_tags"] = Tags(100000000),
                ["tags_as_enum"] = Tags(100000000),
                ["industrycode"] = new OptionSetValue(1),
                ["tags_as_int"] = Tags(100000000),
                ["tags_as_ints"] = Tags(100000000),
            };
            RecordingTracer tracer = new RecordingTracer();

            UnconvertibleShapes projection = ImageProjector.Project<UnconvertibleShapes>(image, tracer)!;

            Assert.Null(projection.StateText);
            Assert.Null(projection.Status);
            Assert.Null(projection.TagSet);
            Assert.Null(projection.SingleTag);
            Assert.Null(projection.IndustryAsList);
            Assert.Null(projection.TagsAsInt);
            Assert.Null(projection.TagsAsInts);
            Assert.Equal(
                new[] { "statecode", "statuscode", "vela_tags", "tags_as_enum", "industrycode", "tags_as_int", "tags_as_ints" }.OrderBy(s => s, StringComparer.Ordinal),
                tracer.Skipped.OrderBy(s => s, StringComparer.Ordinal));
        }

        private sealed class RecordingTracer : ITracingService
        {
            public List<string> Skipped { get; } = new List<string>();

            public void Trace(string format, params object[] args) => Skipped.Add((string)args[0]);
        }
    }
}
