using System;
using System.Linq;
using System.Reflection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Testing.Security;
using Xunit;

namespace Vela.Testing.Tests
{
    // A-62: PluginPipeline forwards the store's fluent configuration by hand, and the copies had drifted
    // (WithStrictSecurity, WithAccessTeamTemplate and WithSharedRecord were missing). These tests pin the parity.
    public sealed class PipelineConfigurationParityTests
    {
        [Fact]
        public void Every_fluent_store_configuration_method_has_a_pipeline_forwarder()
        {
            MethodInfo[] storeConfiguration = typeof(InMemoryOrganizationService)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.ReturnType == typeof(InMemoryOrganizationService))
                .ToArray();

            Assert.NotEmpty(storeConfiguration);

            string[] missing = storeConfiguration
                .Where(storeMethod => !typeof(PluginPipeline)
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Any(pipelineMethod => pipelineMethod.Name == storeMethod.Name
                        && pipelineMethod.ReturnType == typeof(PluginPipeline)
                        && pipelineMethod.IsGenericMethodDefinition == storeMethod.IsGenericMethodDefinition
                        && SameParameterTypes(pipelineMethod, storeMethod)))
                .Select(method => method.Name + "(" + string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name)) + ")")
                .ToArray();

            Assert.True(missing.Length == 0, "PluginPipeline has no forwarder for: " + string.Join("; ", missing));
        }

        [Fact]
        public void Strict_security_forwarded_by_the_pipeline_fails_a_write_without_a_security_model()
        {
            PluginPipeline pipeline = PluginPipeline.Empty().WithStrictSecurity();

            Assert.Throws<InvalidOperationException>(() => pipeline.Service.Create(new Entity("account") { ["name"] = "x" }));
            Assert.Empty(pipeline.GetAll("account"));
        }

        [Fact]
        public void A_record_shared_through_the_pipeline_is_readable_by_the_principal()
        {
            Guid owner = Guid.NewGuid();
            Guid reader = Guid.NewGuid();
            Guid bu = Guid.NewGuid();
            Guid accountId = Guid.NewGuid();
            PluginPipeline pipeline = PluginPipeline.Empty()
                .WithCurrentUser(reader)
                .WithSecurity(s => s
                    .BusinessUnit(bu)
                    .Role("basic", r => r.Grant("account", AccessLevel.User, Privilege.Read))
                    .User(owner, bu, "basic")
                    .User(reader, bu, "basic"))
                .Seed(new Entity("account", accountId) { ["name"] = "Zava", ["ownerid"] = new EntityReference("systemuser", owner) })
                .WithSharedRecord(new EntityReference("account", accountId), new EntityReference("systemuser", reader), Microsoft.Crm.Sdk.Messages.AccessRights.ReadAccess);

            Guid unshared = Guid.NewGuid();
            pipeline.Seed(new Entity("account", unshared) { ["name"] = "Other", ["ownerid"] = new EntityReference("systemuser", owner) });

            Entity read = pipeline.Service.Retrieve("account", accountId, new ColumnSet("name"));

            Assert.Equal("Zava", read["name"]);
            Assert.ThrowsAny<Exception>(() => pipeline.Service.Retrieve("account", unshared, new ColumnSet("name")));
        }

        private static bool SameParameterTypes(MethodInfo left, MethodInfo right)
        {
            Type[] a = left.GetParameters().Select(p => p.ParameterType).ToArray();
            Type[] b = right.GetParameters().Select(p => p.ParameterType).ToArray();
            return a.Length == b.Length && a.Zip(b, (x, y) => x.ToString() == y.ToString()).All(same => same);
        }
    }
}
