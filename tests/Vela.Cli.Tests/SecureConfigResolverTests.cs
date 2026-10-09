using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public class SecureConfigResolverTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string PluginA = "Zava.Plugins.A";
    private const string PluginB = "Zava.Plugins.B";

    private static RegistrationItem Step(string plugin, string secureConfig = "") =>
        new("step", plugin, DefinitionNormalizer.NormalizeStepDefinition(
            JsonSerializer.SerializeToElement(new { pluginTypeName = plugin, message = "Update", stage = 40, mode = 0, stepKey = "main", secureConfig }, Web), Web));

    private static RegistrationState State(RegistrationItem[] items, Dictionary<string, string>? names = null) =>
        new([], [], [], DateTime.UtcNow, items, [], SecureConfigNameByStepKey: names);

    private static string Digest(RegistrationState state, string plugin) =>
        state.Items.Single(i => i.Type == plugin).Definition.GetProperty("secureConfig").GetString()!;

    private static RegistrationState Resolve(RegistrationState local, RegistrationState current, Dictionary<string, string>? supplied = null, Dictionary<string, string>? env = null) =>
        SecureConfigResolver.Resolve(local, current, supplied ?? new(), name => env != null && env.TryGetValue(name, out string? v) ? v : null, Web);

    [Fact]
    public void A_supplied_value_is_written_only_for_the_plugin_that_declares_it()
    {
        // Two plugins share the step key "main"; only A declares a secure configuration.
        RegistrationState local = State([Step(PluginA), Step(PluginB)], new() { [PluginA + ":stepkey:main"] = "PaymentApiKey" });

        RegistrationState resolved = Resolve(local, State([]), supplied: new() { ["PaymentApiKey"] = "s3cret" });

        Assert.Equal(new Dictionary<string, string> { [PluginA + ":stepkey:main"] = "s3cret" }, resolved.SecureConfigByStepKey);
        Assert.Equal(DefinitionNormalizer.HashSecret("s3cret"), Digest(resolved, PluginA));
        Assert.Equal(string.Empty, Digest(resolved, PluginB));
        Assert.DoesNotContain("s3cret", JsonSerializer.Serialize(resolved.Items));
    }

    [Fact]
    public void Without_a_value_the_live_secure_config_is_kept_so_nothing_changes()
    {
        // An admin set B's secure config in the environment and the code declares nothing: no diff, nothing to write.
        RegistrationState current = State([Step(PluginB, secureConfig: "admin-set")]);

        RegistrationState resolved = Resolve(State([Step(PluginB)]), current);

        Assert.Empty(resolved.SecureConfigByStepKey!);
        Assert.Equal(Digest(current, PluginB), Digest(resolved, PluginB));
        Assert.Empty(DiffEngine.BuildDiff(resolved, current).Changed);
    }

    [Fact]
    public void The_environment_variable_supplies_the_value_and_a_flag_wins_over_it()
    {
        RegistrationState local = State([Step(PluginA)], new() { [PluginA + ":stepkey:main"] = "Key" });
        Dictionary<string, string> env = new() { [SecureConfigResolver.EnvironmentVariablePrefix + "Key"] = "from-env" };

        Assert.Equal("from-env", Resolve(local, State([]), env: env).SecureConfigByStepKey![PluginA + ":stepkey:main"]);
        Assert.Equal("from-flag", Resolve(local, State([]), supplied: new() { ["Key"] = "from-flag" }, env: env).SecureConfigByStepKey![PluginA + ":stepkey:main"]);
    }

    [Fact]
    public void A_declared_configuration_with_no_value_anywhere_is_reported_without_writing()
    {
        RegistrationState local = State([Step(PluginA)], new() { [PluginA + ":stepkey:main"] = "Key" });

        RegistrationState resolved = Resolve(local, State([]));

        Assert.Empty(resolved.SecureConfigByStepKey!);
        Assert.Contains(resolved.Diagnostics, d => d.Contains("'Key'") && d.Contains("--secure-config Key=VALUE"));
    }

    [Fact]
    public void ParseSpecs_reads_values_and_files_and_never_echoes_a_value()
    {
        Dictionary<string, string> values = SecureConfigResolver.ParseSpecs(["A=x=y", "B=@secret.txt", "C="], path => path == "secret.txt" ? "from-file" : throw new IOException("missing"));

        Assert.Equal("x=y", values["A"]);
        Assert.Equal("from-file", values["B"]);
        Assert.Equal(string.Empty, values["C"]); // explicit empty clears the live secure config

        ArgumentException bad = Assert.Throws<ArgumentException>(() => SecureConfigResolver.ParseSpecs(["bad name=hunter2"], _ => ""));
        Assert.DoesNotContain("hunter2", bad.Message);
        Assert.Throws<ArgumentException>(() => SecureConfigResolver.ParseSpecs(["A=1", "A=2"], _ => ""));
        Assert.Throws<FileNotFoundException>(() => SecureConfigResolver.ParseSpecs(["A=@nope"], _ => throw new IOException("missing")));
    }

    [Fact]
    public void Deleting_a_step_also_deletes_its_secure_config_row()
    {
        Guid stepId = Guid.NewGuid(), configId = Guid.NewGuid();
        var mock = new Mock<IOrganizationService>(MockBehavior.Loose);
        mock.Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns((QueryBase q) => (q as QueryExpression)?.EntityName == "sdkmessageprocessingstep"
                ? new EntityCollection([new Entity("sdkmessageprocessingstep", stepId)])
                : new EntityCollection());
        mock.Setup(s => s.Retrieve("sdkmessageprocessingstep", stepId, It.IsAny<ColumnSet>()))
            .Returns(new Entity("sdkmessageprocessingstep", stepId) { ["sdkmessageprocessingstepsecureconfigid"] = new EntityReference("sdkmessageprocessingstepsecureconfig", configId) });

        DataverseRegistration.DeleteStepByDefinition(mock.Object,
            JsonSerializer.SerializeToElement(new { pluginTypeName = PluginA, name = "MyStep", message = "Update", stage = 40, mode = 0, stepKey = "k" }, Web),
            new Dictionary<string, Guid> { [PluginA] = Guid.NewGuid() });

        mock.Verify(s => s.Delete("sdkmessageprocessingstep", stepId), Times.Once);
        mock.Verify(s => s.Delete("sdkmessageprocessingstepsecureconfig", configId), Times.Once);
    }

    [Fact]
    public void A_raw_value_that_looks_like_a_digest_is_still_hashed()
    {
        // A secret starting with the digest prefix used to pass through HashSecret verbatim into plan output.
        const string tricky = "hmac-sha256:private-token";
        RegistrationState local = State([Step(PluginA)], new() { [PluginA + ":stepkey:main"] = "Key" });

        RegistrationState resolved = Resolve(local, State([]), supplied: new() { ["Key"] = tricky });

        Assert.NotEqual(tricky, Digest(resolved, PluginA));
        Assert.DoesNotContain("private-token", JsonSerializer.Serialize(resolved.Items));
        Assert.NotEqual(tricky, DefinitionNormalizer.HashSecret(tricky)); // the live read hashes through the same call
    }

    [Fact]
    public void Normalizing_never_trusts_a_raw_secure_config_into_the_shape()
    {
        // Only an already-normalized digest survives (re-normalization is a fixed point); a literal is dropped.
        string digest = DefinitionNormalizer.HashSecret("s3cret");
        JsonElement fromLiteral = DefinitionNormalizer.NormalizeStepDefinition(
            JsonSerializer.SerializeToElement(new { pluginTypeName = PluginA, message = "Update", stage = 40, mode = 0, secureConfig = "hmac-sha256:private-token" }, Web), Web);
        JsonElement fromDigest = DefinitionNormalizer.NormalizeStepDefinition(
            JsonSerializer.SerializeToElement(new { pluginTypeName = PluginA, message = "Update", stage = 40, mode = 0, secureConfig = digest }, Web), Web);

        Assert.Equal(string.Empty, fromLiteral.GetProperty("secureConfig").GetString());
        Assert.Equal(digest, fromDigest.GetProperty("secureConfig").GetString());
    }

    [Fact]
    public void A_keyless_step_keeps_its_live_secure_config_like_the_diff_pairs_it()
    {
        // No step key: the diff pairs local and live by legacy identity, so must the resolver, or every plan shows a
        // phantom secure-config change.
        static RegistrationItem Keyless(string secureConfig) =>
            new("step", PluginA, DefinitionNormalizer.BuildStepShape(PluginA, "Update", "account", "", "n", "n", 40, 0, 1, [], [], [], true, "", secureConfig, null, 0, Web));
        RegistrationState current = State([Keyless(DefinitionNormalizer.HashSecret("admin-set"))]);

        RegistrationState resolved = Resolve(State([Keyless("")]), current);

        Assert.Empty(DiffEngine.BuildDiff(resolved, current).Changed);
    }

    [Fact]
    public void ParseSpecs_never_echoes_a_bare_value()
    {
        // A secret passed alone (--secure-config "$API_KEY") must not reach the error message.
        ArgumentException ex = Assert.Throws<ArgumentException>(() => SecureConfigResolver.ParseSpecs(["sk-live-1234"], _ => ""));
        Assert.DoesNotContain("sk-live-1234", ex.Message);
    }

    [Fact]
    public void A_failed_step_delete_keeps_its_secure_config_on_the_batched_path()
    {
        // The secure-config delete runs in a later tier and is skipped when its step's delete fails, so a step that
        // survives keeps its secret.
        Guid stepId = Guid.NewGuid(), configId = Guid.NewGuid(), pluginTypeId = Guid.NewGuid();
        var svc = new TestSupport.FakeOrgService
        {
            OnRetrieveMultiple = q => q.EntityName switch
            {
                "pluginassembly" => TestSupport.FakeOrgService.Collection(new Entity("pluginassembly", Guid.NewGuid())),
                "plugintype" => TestSupport.FakeOrgService.Collection(new Entity("plugintype", pluginTypeId) { ["typename"] = PluginA }),
                "sdkmessageprocessingstep" => TestSupport.FakeOrgService.Collection(new Entity("sdkmessageprocessingstep", stepId)),
                _ => new EntityCollection(),
            },
            OnRetrieve = (e, id, _) => new Entity(e, id) { ["sdkmessageprocessingstepsecureconfigid"] = new EntityReference("sdkmessageprocessingstepsecureconfig", configId) },
            OnDelete = (e, _) =>
            {
                if (e == "sdkmessageprocessingstep")
                {
                    throw new System.ServiceModel.FaultException<OrganizationServiceFault>(new OrganizationServiceFault { ErrorCode = -1, Message = "locked" });
                }
            },
        };
        var diff = new RegistrationDiff(["Zava.Plugins"], ["Zava.Plugins"], [], [Step(PluginA)], [], [], []);

        Assert.ThrowsAny<Exception>(() => DataverseRegistration.ApplyDiff(svc, diff, [], null, [], verbose: false));

        Assert.DoesNotContain(svc.Deleted, d => d.Entity == "sdkmessageprocessingstepsecureconfig");
    }
}
