using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// <c>vela models --verify --roles</c> / <c>--solution-roles</c>: the Roles class on disk against the roles of the
/// target environment (an in-memory <see cref="RoleOrg"/>), compared on each role's root id and its grants.
/// </summary>
public sealed class RoleVerifyTests
{
    private const string Ns = "Zava.Shared.Models.Generated";

    [Fact]
    public void A_roles_class_generated_from_the_same_environment_is_in_sync()
    {
        RoleOrg org = new();
        org.AddRole("Sales Rep", (RoleOrg.PrvReadAccount, 8), (RoleOrg.PrvReadContact, 1));
        org.AddRole("Support", (RoleOrg.PrvReadContact, 2));
        string local = RolesClass(RoleReader.ReadRoles(org, ["Sales Rep", "Support"], null));

        RoleDriftChecker.RoleDriftReport report = ModelsCommand.VerifyRoles(org, ["Sales Rep", "Support"], null, Ns, [local]);

        Assert.False(report.HasDrift);
        Assert.False(report.MissingLocalClass);
        Assert.Equal(new[] { "Sales Rep", "Support" }, report.Roles.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void A_different_root_id_and_different_privileges_are_drift_listing_each_difference()
    {
        RoleOrg org = new();
        Guid live = org.AddRole("Sales Rep", (RoleOrg.PrvReadAccount, 8), (RoleOrg.PrvReadContact, 8), (RoleOrg.PrvWriteContact, 1));
        Guid local = Guid.NewGuid();
        string source = RolesClass(
        [
            new RoleModel("Sales Rep", local,
            [
                new RoleGrantModel("account", "Read", "Organization"),
                new RoleGrantModel("contact", "Read", "User"),
                new RoleGrantModel("opportunity", "Read", "BusinessUnit"),
            ]),
        ]);

        RoleDriftChecker.RoleDriftReport report = ModelsCommand.VerifyRoles(org, ["Sales Rep"], null, Ns, ["// another model file", source]);

        Assert.True(report.HasDrift);
        RoleDriftChecker.RoleDrift drift = Assert.Single(report.Roles);
        Assert.True(drift.IdChanged);
        Assert.Equal(local, drift.LocalId);
        Assert.Equal(live, drift.LiveId);
        Assert.Equal(new RoleDriftChecker.Grant("opportunity", "Read", "BusinessUnit"), Assert.Single(drift.RemovedInDataverse));
        Assert.Equal(new RoleDriftChecker.LevelChange("contact", "Read", "User", "Organization"), Assert.Single(drift.LevelChanged));
        // customeraddress shares prvReadAccount, so the environment grants it too.
        Assert.Equal(
            new[] { new RoleDriftChecker.Grant("contact", "Write", "User"), new RoleDriftChecker.Grant("customeraddress", "Read", "Organization") },
            drift.AddedInDataverse.ToArray());
    }

    [Fact]
    public void Only_the_root_id_differing_is_drift()
    {
        RoleOrg org = new();
        org.AddRole("Sales Rep", (RoleOrg.PrvReadContact, 1));
        string source = RolesClass([new RoleModel("Sales Rep", Guid.NewGuid(), [new RoleGrantModel("contact", "Read", "User")])]);

        RoleDriftChecker.RoleDrift drift = Assert.Single(ModelsCommand.VerifyRoles(org, ["Sales Rep"], null, Ns, [source]).Roles);

        Assert.True(drift.HasDrift);
        Assert.True(drift.IdChanged);
        Assert.Empty(drift.RemovedInDataverse);
        Assert.Empty(drift.LevelChanged);
        Assert.Empty(drift.AddedInDataverse);
    }

    [Fact]
    public void A_requested_role_the_environment_lacks_is_drift_not_a_failure()
    {
        RoleOrg org = new();
        string source = RolesClass([new RoleModel("Sales Rep", Guid.NewGuid(), [])]);

        RoleDriftChecker.RoleDriftReport report = ModelsCommand.VerifyRoles(org, ["Sales Rep", "Typo"], null, Ns, [source]);

        Assert.True(report.HasDrift);
        RoleDriftChecker.RoleDrift sales = report.Roles.Single(r => r.Name == "Sales Rep");
        Assert.True(sales.MissingInDataverse);
        Assert.False(sales.MissingLocally);
        Assert.NotNull(sales.LocalId);
        RoleDriftChecker.RoleDrift typo = report.Roles.Single(r => r.Name == "Typo");
        Assert.True(typo.MissingInDataverse);
        Assert.True(typo.MissingLocally);
    }

    [Fact]
    public void Solution_roles_flag_a_role_the_class_lacks_and_one_the_solution_no_longer_holds()
    {
        RoleOrg org = new();
        Guid solution = org.AddSolution("ZavaCore");
        org.AddComponent(solution, org.AddRole("New Role", (RoleOrg.PrvReadContact, 1)), componentType: 20);
        org.AddRole("Old Role", (RoleOrg.PrvReadContact, 1)); // still in the org, no longer in the solution
        string source = RolesClass([new RoleModel("Old Role", Guid.NewGuid(), [new RoleGrantModel("contact", "Read", "User")])]);

        RoleDriftChecker.RoleDriftReport report = ModelsCommand.VerifyRoles(org, [], "ZavaCore", Ns, [source]);

        Assert.Equal(new[] { "New Role", "Old Role" }, report.Roles.Select(r => r.Name).ToArray());
        Assert.True(report.Roles[0].MissingLocally);
        Assert.False(report.Roles[0].MissingInDataverse);
        Assert.True(report.Roles[1].MissingInDataverse);
        Assert.False(report.Roles[1].MissingLocally);
    }

    [Fact]
    public void No_roles_class_on_disk_is_drift()
    {
        RoleOrg org = new();
        org.AddRole("Sales Rep", (RoleOrg.PrvReadContact, 1));

        RoleDriftChecker.RoleDriftReport report = ModelsCommand.VerifyRoles(org, ["Sales Rep"], null, Ns, ["public sealed class Account { }"]);

        Assert.True(report.MissingLocalClass);
        Assert.True(report.HasDrift);
        Assert.True(Assert.Single(report.Roles).MissingLocally);
    }

    [Fact]
    public void Role_names_are_matched_whatever_their_case()
    {
        RoleOrg org = new();
        org.AddRole("Sales Rep", (RoleOrg.PrvReadContact, 1));
        string source = RolesClass(RoleReader.ReadRoles(org, ["Sales Rep"], null)).Replace("\"Sales Rep\"", "\"sales rep\"");

        Assert.False(ModelsCommand.VerifyRoles(org, ["Sales Rep"], null, Ns, [source]).HasDrift);
    }

    [Fact]
    public void Extract_reads_back_what_the_generator_writes_including_escaped_names_and_empty_grants()
    {
        Guid quoted = Guid.NewGuid();
        Guid empty = Guid.NewGuid();
        string source = RolesClass(
        [
            new RoleModel("Say \"hi\" \\ now", quoted, [new RoleGrantModel("contact", "Read", "User"), new RoleGrantModel("account", "Write", "Organization")]),
            new RoleModel("2nd Line", empty, []),
            new RoleModel("Role Name", Guid.NewGuid(), []),
        ]);

        IReadOnlyList<RoleDriftChecker.ExtractedRole> roles = RoleDriftChecker.Extract(source)!;

        Assert.Equal(new[] { "2nd Line", "Role Name", "Say \"hi\" \\ now" }, roles.Select(r => r.Name).ToArray());
        Assert.Equal(empty, roles[0].Id);
        Assert.Empty(roles[0].Grants);
        Assert.Empty(roles[1].Grants);
        Assert.Equal(quoted, roles[2].Id);
        Assert.Equal(
            new[] { new RoleDriftChecker.Grant("account", "Write", "Organization"), new RoleDriftChecker.Grant("contact", "Read", "User") },
            roles[2].Grants.ToArray());
    }

    [Fact]
    public void Extract_reads_a_system_roles_template_id_without_taking_it_for_another_roles_id()
    {
        Guid adminId = Guid.NewGuid();
        Guid template = Guid.NewGuid();
        Guid adminTemplateRoleId = Guid.NewGuid();
        // "System Administrator Template" is a role whose Id member (SystemAdministratorTemplateId) is spelled like the
        // template id member of "System Administrator"; the generator orders them so each follows its own Name.
        string source = RolesClass(
        [
            new RoleModel("System Administrator", adminId, [new RoleGrantModel("account", "Read", "Organization")], template),
            new RoleModel("Sales Rep", Guid.NewGuid(), []),
        ]);

        IReadOnlyList<RoleDriftChecker.ExtractedRole> roles = RoleDriftChecker.Extract(source)!;

        Assert.Contains($"public static readonly Guid SystemAdministratorTemplateId = new Guid(\"{template:D}\");", source);
        RoleDriftChecker.ExtractedRole admin = roles.Single(r => r.Name == "System Administrator");
        Assert.Equal(adminId, admin.Id);
        Assert.Equal(template, admin.TemplateId);
        Assert.Equal("account", Assert.Single(admin.Grants).Entity);
        Assert.Null(roles.Single(r => r.Name == "Sales Rep").TemplateId);

        string lookalike = RolesClass([new RoleModel("System Administrator Template", adminTemplateRoleId, [])]);
        RoleDriftChecker.ExtractedRole role = Assert.Single(RoleDriftChecker.Extract(lookalike)!);
        Assert.Equal(adminTemplateRoleId, role.Id);
        Assert.Null(role.TemplateId);
    }

    [Fact]
    public void A_system_role_in_sync_keeps_its_template_id()
    {
        RoleOrg org = new();
        Guid admin = org.AddRole("System Administrator", (RoleOrg.PrvReadAccount, 8));
        org.SetTemplate(admin, Guid.NewGuid());
        string local = RolesClass(RoleReader.ReadRoles(org, ["System Administrator"], null));

        Assert.Contains("SystemAdministratorTemplateId", local);
        Assert.False(ModelsCommand.VerifyRoles(org, ["System Administrator"], null, Ns, [local]).HasDrift);
    }

    [Theory]
    [InlineData(true, true)]   // a different template id
    [InlineData(true, false)]  // the class has one, the environment's role has none
    [InlineData(false, true)]  // the environment's role is a system role, the class has no template id
    public void A_differing_template_id_is_drift(bool localHasTemplate, bool liveHasTemplate)
    {
        RoleOrg org = new();
        Guid admin = org.AddRole("System Administrator", (RoleOrg.PrvReadAccount, 8));
        Guid liveTemplate = Guid.NewGuid();
        if (liveHasTemplate)
        {
            org.SetTemplate(admin, liveTemplate);
        }

        Guid? localTemplate = localHasTemplate ? Guid.NewGuid() : null;
        RoleModel live = Assert.Single(RoleReader.ReadRoles(org, ["System Administrator"], null));
        string source = RolesClass([live with { TemplateId = localTemplate }]);

        RoleDriftChecker.RoleDrift drift = Assert.Single(ModelsCommand.VerifyRoles(org, ["System Administrator"], null, Ns, [source]).Roles);

        Assert.True(drift.HasDrift);
        Assert.True(drift.TemplateIdChanged);
        Assert.False(drift.IdChanged);
        Assert.Equal(localTemplate, drift.LocalTemplateId);
        Assert.Equal(liveHasTemplate ? liveTemplate : null, drift.LiveTemplateId);
        Assert.Empty(drift.RemovedInDataverse);
        Assert.Empty(drift.AddedInDataverse);
    }

    [Fact]
    public void Extract_reads_a_grant_tuple_with_whitespace_after_the_parenthesis()
    {
        RoleOrg org = new();
        org.AddRole("Sales Rep", (RoleOrg.PrvReadContact, 1));
        string generated = RolesClass(RoleReader.ReadRoles(org, ["Sales Rep"], null));
        // Hand-formatted: a space (and a tab) between the parenthesis and the first literal.
        string spaced = generated.Replace("(\"contact\",", "( \t\"contact\",");
        Assert.NotEqual(generated, spaced);

        RoleDriftChecker.ExtractedRole role = Assert.Single(RoleDriftChecker.Extract(spaced)!);

        Assert.Equal(new RoleDriftChecker.Grant("contact", "Read", "User"), Assert.Single(role.Grants));
        Assert.False(ModelsCommand.VerifyRoles(org, ["Sales Rep"], null, Ns, [spaced]).HasDrift);
    }

    [Fact]
    public void Two_root_roles_sharing_a_name_are_each_matched_on_their_root_id()
    {
        RoleOrg org = new();
        Guid solution = org.AddSolution("ZavaCore");
        org.AddComponent(solution, org.AddRole("Sales Rep", (RoleOrg.PrvReadContact, 1)), componentType: 20);
        org.AddComponent(solution, org.AddRole("Sales Rep", (RoleOrg.PrvReadAccount, 8)), componentType: 20);
        string local = RolesClass(RoleReader.ReadRoles(org, [], "ZavaCore"));

        RoleDriftChecker.RoleDriftReport report = ModelsCommand.VerifyRoles(org, [], "ZavaCore", Ns, [local]);

        Assert.Equal(2, RoleDriftChecker.Extract(local)!.Count);
        Assert.False(report.HasDrift);
        Assert.Equal(2, report.Roles.Count);
        Assert.All(report.Roles, r => Assert.Equal(r.LocalId, r.LiveId));
    }

    [Fact]
    public void Same_name_roles_that_cannot_be_paired_by_root_id_are_each_reported_missing()
    {
        RoleOrg org = new();
        Guid solution = org.AddSolution("ZavaCore");
        Guid liveA = org.AddRole("Sales Rep", (RoleOrg.PrvReadContact, 1));
        Guid liveB = org.AddRole("Sales Rep", (RoleOrg.PrvReadAccount, 8));
        org.AddComponent(solution, liveA, componentType: 20);
        org.AddComponent(solution, liveB, componentType: 20);
        Guid localA = Guid.NewGuid();
        Guid localB = Guid.NewGuid();
        string source = RolesClass([new RoleModel("Sales Rep", localA, []), new RoleModel("Sales Rep", localB, [])]);

        RoleDriftChecker.RoleDriftReport report = ModelsCommand.VerifyRoles(org, [], "ZavaCore", Ns, [source]);

        Assert.True(report.HasDrift);
        Assert.Equal(4, report.Roles.Count);
        Assert.DoesNotContain(report.Roles, r => r.IdChanged);
        Assert.Equal(
            new[] { liveA, liveB }.OrderBy(id => id).ToArray(),
            report.Roles.Where(r => r.MissingLocally).Select(r => r.LiveId!.Value).OrderBy(id => id).ToArray());
        Assert.Equal(
            new[] { localA, localB }.OrderBy(id => id).ToArray(),
            report.Roles.Where(r => r.MissingInDataverse).Select(r => r.LocalId!.Value).OrderBy(id => id).ToArray());
    }

    [Fact]
    public void Same_name_roles_get_the_same_identifiers_whatever_order_they_resolve_in()
    {
        RoleModel first = new("Sales Rep", new Guid("00000000-0000-0000-0000-000000000001"), []);
        RoleModel second = new("Sales Rep", new Guid("00000000-0000-0000-0000-000000000002"), []);

        Assert.Equal(RolesClass([first, second]), RolesClass([second, first]));
    }

    [Fact]
    public void Solution_roles_of_a_solution_without_tables_are_verified()
    {
        RoleOrg org = new();
        Guid solution = org.AddSolution("ZavaCore");
        org.AddComponent(solution, org.AddRole("Sales Rep", (RoleOrg.PrvReadContact, 1)), componentType: 20);
        ModelsScope scope = ModelsScope.Parse(["--solution", "ZavaCore", "--solution-roles"]);
        string local = RolesClass(RoleReader.ReadRoles(org, [], "ZavaCore"));

        ModelsCommand.VerifyResult inSync = ModelsCommand.VerifyScope(org, scope, Ns, dataAnnotations: false, [local]);
        ModelsCommand.VerifyResult drifted = ModelsCommand.VerifyScope(org, scope, Ns, dataAnnotations: false, [local.Replace("\"User\"", "\"Organization\"")]);

        Assert.Null(inSync.Error);
        Assert.Null(inSync.Tables);
        Assert.False(inSync.Roles!.HasDrift);
        Assert.Equal(CliExitCodes.ExitSuccess, inSync.ExitCode);
        Assert.True(drifted.Roles!.HasDrift);
        Assert.Equal(CliExitCodes.ExitDriftDetected, drifted.ExitCode);
    }

    [Fact]
    public void A_solution_without_tables_and_no_role_scope_is_still_an_error()
    {
        RoleOrg org = new();
        org.AddSolution("ZavaCore");

        ModelsCommand.VerifyResult result = ModelsCommand.VerifyScope(org, ModelsScope.Parse(["--solution", "ZavaCore"]), Ns, dataAnnotations: false, []);

        Assert.Equal("No tables resolved from the given scope.", result.Error);
        Assert.Equal(CliExitCodes.ExitInvalidArguments, result.ExitCode);
    }

    [Fact]
    public void Extract_ignores_a_file_without_a_roles_class()
    {
        Assert.Null(RoleDriftChecker.Extract("namespace N { public sealed class Account { public const string EntityLogicalNameValue = \"account\"; } }"));
        Assert.Null(RoleDriftChecker.Extract(string.Empty));
    }

    [Fact]
    public void ReadRoles_with_a_collector_returns_the_missing_names_instead_of_failing()
    {
        List<string> notFound = [];

        IReadOnlyList<RoleModel> roles = RoleReader.ReadRoles(new RoleOrg(), ["Ghost"], null, notFound);

        Assert.Empty(roles);
        Assert.Equal(new[] { "Ghost" }, notFound.ToArray());
    }

    [Fact]
    public void BuildDriftReport_adds_the_roles_and_their_differences_to_the_json_contract()
    {
        Guid local = Guid.NewGuid();
        Guid live = Guid.NewGuid();
        RoleDriftChecker.RoleDriftReport roles = new(MissingLocalClass: false,
        [
            new RoleDriftChecker.RoleDrift("Sales Rep", MissingInDataverse: false, MissingLocally: false, local, live,
                [new RoleDriftChecker.Grant("opportunity", "Read", "User")],
                [new RoleDriftChecker.LevelChange("contact", "Read", "User", "Organization")],
                [new RoleDriftChecker.Grant("contact", "Write", "User")],
                LocalTemplateId: null,
                LiveTemplateId: live),
            new RoleDriftChecker.RoleDrift("Support", false, false, live, live, [], [], []),
        ]);

        JsonElement root = Serialize(ModelsCommand.BuildDriftReport("https://org.crm.dynamics.com", Ns, report: null, roles));

        Assert.True(root.GetProperty("hasDrift").GetBoolean());
        Assert.Equal(0, root.GetProperty("tables").GetArrayLength());
        Assert.False(root.GetProperty("missingRolesClass").GetBoolean());
        JsonElement sales = root.GetProperty("roles")[0];
        Assert.Equal("Sales Rep", sales.GetProperty("name").GetString());
        Assert.False(sales.GetProperty("inSync").GetBoolean());
        Assert.True(sales.GetProperty("idChanged").GetBoolean());
        Assert.Equal(local, sales.GetProperty("localId").GetGuid());
        Assert.Equal(live, sales.GetProperty("liveId").GetGuid());
        Assert.True(sales.GetProperty("templateIdChanged").GetBoolean());
        Assert.Equal(JsonValueKind.Null, sales.GetProperty("localTemplateId").ValueKind);
        Assert.Equal(live, sales.GetProperty("liveTemplateId").GetGuid());
        Assert.Equal("opportunity", sales.GetProperty("removedInDataverse")[0].GetProperty("entity").GetString());
        JsonElement change = sales.GetProperty("levelChanged")[0];
        Assert.Equal("User", change.GetProperty("localLevel").GetString());
        Assert.Equal("Organization", change.GetProperty("liveLevel").GetString());
        Assert.Equal("Write", sales.GetProperty("addedInDataverse")[0].GetProperty("privilege").GetString());
        Assert.True(root.GetProperty("roles")[1].GetProperty("inSync").GetBoolean());
    }

    [Fact]
    public void BuildDriftReport_without_a_role_scope_has_no_roles_entry()
    {
        JsonElement root = Serialize(ModelsCommand.BuildDriftReport(null, Ns, new ModelDriftChecker.ModelDriftReport([])));

        Assert.False(root.TryGetProperty("roles", out _));
        Assert.False(root.TryGetProperty("missingRolesClass", out _));
        Assert.False(root.GetProperty("hasDrift").GetBoolean());
    }

    private static string RolesClass(IEnumerable<RoleModel> roles) => RoleGenerator.Generate(roles, new RoleGenOptions(Ns)).Code;

    private static JsonElement Serialize(object dto)
    {
        string json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
