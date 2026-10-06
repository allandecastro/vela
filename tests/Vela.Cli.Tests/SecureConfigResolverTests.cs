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
            JsonSerializer.SerializeToElement(new { pluginTypeName = PluginA, name = "MyStep", message = "Update", stage = 40, mode = 0, stepKey = "k" }, Web));

        mock.Verify(s => s.Delete("sdkmessageprocessingstep", stepId), Times.Once);
        mock.Verify(s => s.Delete("sdkmessageprocessingstepsecureconfig", configId), Times.Once);
    }
}
