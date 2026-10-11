using System;
using Microsoft.Xrm.Sdk;
using Vela.Context;
using Xunit;

namespace Vela.Testing.Tests
{
    // The PluginContextBuilder setters no other test exercised (A-82). Each checks that the value reaches the
    // context a plugin sees: the LocalPluginContext the builder builds and the raw IPluginExecutionContext under it.
    public sealed class PluginContextBuilderPublicApiTests
    {
        [Fact]
        public void WithUserId_sets_the_calling_user_the_plugin_sees()
        {
            Guid user = new Guid("2b5a0e9e-7a4f-4f0e-8d2c-6c1b9a3e4d51");

            LocalPluginContext ctx = PluginContext.For<FakeAccount>().WithUserId(user).Build();

            Assert.Equal(user, ctx.UserId);
            Assert.Equal(user, ctx.PluginExecutionContext.UserId);
        }

        [Fact]
        public void WithCorrelationId_sets_the_correlation_id_the_plugin_sees()
        {
            Guid correlation = new Guid("9d3f4c1a-0b6e-4a2d-b8f7-1e5c3a9d7b20");

            LocalPluginContext ctx = PluginContext.For<FakeAccount>().WithCorrelationId(correlation).Build();

            Assert.Equal(correlation, ctx.CorrelationId);
            Assert.Equal(correlation, ctx.PluginExecutionContext.CorrelationId);
        }

        [Fact]
        public void WithBulkPostImages_exposes_one_post_image_per_target_index()
        {
            FakeAccount first = new FakeAccount { Id = Guid.NewGuid(), Name = "First" };
            FakeAccount second = new FakeAccount { Id = Guid.NewGuid(), Name = "Second" };

            LocalPluginContext ctx = PluginContext.For<FakeAccount>()
                .OnMessage("UpdateMultiple")
                .WithBulkPostImages(
                    new EntityImageCollection { ["PostImage"] = first },
                    new EntityImageCollection { ["PostImage"] = second })
                .Build();

            Assert.Equal(2, ctx.BulkPostEntityImagesCollection.Count);
            Assert.Same(first, ctx.GetPostImage(0));
            Assert.Same(second, ctx.GetPostImage(1));
            Assert.Null(ctx.GetPostImage(1, "Other"));
            // Post images only: the pre-image side stays empty.
            Assert.Empty(ctx.BulkPreEntityImagesCollection);
        }

        [Fact]
        public void WithOutputParameter_is_read_back_as_an_output_parameter()
        {
            Guid created = Guid.NewGuid();

            LocalPluginContext ctx = PluginContext.For<FakeAccount>()
                .OnCreate()
                .WithOutputParameter("id", created)
                .Build();

            Assert.Equal(created, ctx.GetOutputParameter<Guid>("id"));
            Assert.Equal(created, ctx.PluginExecutionContext.OutputParameters["id"]);
            // An output parameter is not an input parameter.
            Assert.False(ctx.PluginExecutionContext.InputParameters.Contains("id"));
        }

        [Fact]
        public void WithEnvironmentId_sets_the_environment_id_on_the_execution_context()
        {
            const string environment = "7c2e5b1d-3f4a-4e6b-9a8c-0d1e2f3a4b5c";

            LocalPluginContext ctx = PluginContext.For<FakeAccount>().WithEnvironmentId(environment).Build();

            // The SDK types EnvironmentId as a string (IPluginExecutionContext6).
            IPluginExecutionContext6 raw = Assert.IsAssignableFrom<IPluginExecutionContext6>(ctx.PluginExecutionContext);
            Assert.Equal(environment, raw.EnvironmentId);
        }

        // ILocalPluginContext.EnvironmentId surfaces the platform's string as-is.
        [Fact]
        public void WithEnvironmentId_is_exposed_on_the_local_context()
        {
            string environment = "7c2e5b1d-3f4a-4e6b-9a8c-0d1e2f3a4b5c";

            LocalPluginContext ctx = PluginContext.For<FakeAccount>().WithEnvironmentId(environment).Build();

            Assert.Equal(environment, ctx.EnvironmentId);
        }

        [Fact]
        public void AsApplicationUser_marks_the_caller_as_an_application_user()
        {
            Assert.False(PluginContext.For<FakeAccount>().Build().IsApplicationUser);

            LocalPluginContext ctx = PluginContext.For<FakeAccount>().AsApplicationUser().Build();
            Assert.True(ctx.IsApplicationUser);
            Assert.True(Assert.IsAssignableFrom<IPluginExecutionContext7>(ctx.PluginExecutionContext).IsApplicationUser);

            // Explicit false turns it back off.
            Assert.False(PluginContext.For<FakeAccount>().AsApplicationUser().AsApplicationUser(false).Build().IsApplicationUser);
        }
    }
}
