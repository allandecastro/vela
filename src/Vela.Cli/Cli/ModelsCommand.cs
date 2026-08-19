using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using static Vela.Cli.CliExitCodes;
using static Vela.Cli.CommandLineOptions;
using static Vela.Cli.OutputWriter;

namespace Vela.Cli;

/// <summary>
/// Implements <c>vela models</c>: reads table metadata over the same Dataverse connection as plan/apply and
/// generates early-bound classes via <see cref="ModelGenerator"/>. Scope by <c>--solution</c> (derived from the
/// solution's components — what <c>pac modelbuilder</c> can't do) and/or an explicit <c>--entities</c> list.
/// </summary>
internal static class ModelsCommand
{
    public static int Run(string[] args)
    {
        if (args.Length > 0 && args[0] is "--help" or "-h" or "-?" or "/?" or "help")
        {
            PrintModelsUsage();
            return ExitSuccess;
        }

        string? solution = TryReadOption(args, "--solution");
        string[] entities = (TryReadOption(args, "--entities") ?? string.Empty)
            .Split([','], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToArray();

        string[] customApis = (TryReadOption(args, "--custom-apis") ?? string.Empty)
            .Split([','], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToArray();

        string[] roles = (TryReadOption(args, "--roles") ?? string.Empty)
            .Split([','], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // --solution-roles pulls every security role in --solution (like --solution does for tables).
        bool solutionRoles = HasFlag(args, "--solution-roles");

        if (string.IsNullOrWhiteSpace(solution) && entities.Length == 0 && customApis.Length == 0 && roles.Length == 0 && !solutionRoles)
        {
            return Fail("Specify a scope: --solution <uniquename>, --entities <a,b,c>, --custom-apis <a,b,c>, --roles <a,b,c> and/or --solution-roles. (Refusing to generate every table.)", ExitInvalidArguments, includeUsage: false);
        }

        if (solutionRoles && string.IsNullOrWhiteSpace(solution))
        {
            return Fail("--solution-roles requires --solution <uniquename>.", ExitInvalidArguments, includeUsage: false);
        }

        string? ns = ResolveNamespace(args);
        if (string.IsNullOrWhiteSpace(ns))
        {
            return Fail("Could not infer a namespace. Pass --namespace <ns> (or run inside a project so it can be derived).", ExitInvalidArguments, includeUsage: false);
        }

        string outDir = Path.GetFullPath(TryReadOption(args, "--out") ?? ".");
        bool dataAnnotations = !HasFlag(args, "--no-annotations");
        bool overwrite = HasFlag(args, "--overwrite");
        bool interactive = HasFlag(args, "--interactive") || HasFlag(args, "-i");

        if (!TryResolveConnection(args, interactive, out string? connectionString, out string? oauthUrl, out string? oauthToken, out string? url, out int exitCode))
        {
            return exitCode;
        }

        SharedOptions options = new SharedOptions(
            PackagePaths: [],
            PackageAssembly: null,
            ConnectionString: connectionString,
            OAuthUrl: oauthUrl,
            OAuthToken: oauthToken,
            SolutionUniqueName: solution,
            Environment: url,
            OutPath: outDir,
            Execute: false,
            Interactive: interactive,
            Strict: false,
            Verbose: HasFlag(args, "--verbose") || HasFlag(args, "-v"));

        try
        {
            using ServiceClient client = DataverseRegistration.CreateDataverseClient(options);
            IOrganizationService service = client;

            List<GeneratedModel> models = new List<GeneratedModel>();

            List<string> logicalNames = ResolveLogicalNames(service, solution, entities);
            if (logicalNames.Count > 0)
            {
                EntityMetadata[] metadata = logicalNames.Select(name => ReadEntity(service, name)).ToArray();
                models.AddRange(ModelGenerator.Generate(metadata, new ModelGenOptions(ns!, dataAnnotations)));
            }

            if (customApis.Length > 0)
            {
                CustomApiMessage[] messages = customApis.Select(name => ReadCustomApi(service, name)).ToArray();
                models.AddRange(CustomApiMessageGenerator.Generate(messages, ns!));
            }

            if (roles.Length > 0 || solutionRoles)
            {
                IReadOnlyList<RoleModel> roleModels = ReadRoles(service, roles, solutionRoles ? solution : null);
                if (roleModels.Count > 0)
                {
                    models.Add(RoleGenerator.Generate(roleModels, new RoleGenOptions(ns!)));
                }
            }

            if (models.Count == 0)
            {
                return Fail("Nothing resolved from the given scope.", ExitInvalidArguments, includeUsage: false);
            }

            Directory.CreateDirectory(outDir);

            // Fail up front on any pre-existing file so generation is all-or-nothing: writing some files and
            // then aborting mid-loop would leave a half-generated output directory.
            if (!overwrite)
            {
                List<string> existing = models
                    .Select(model => Path.Combine(outDir, model.FileName))
                    .Where(File.Exists)
                    .ToList();
                if (existing.Count > 0)
                {
                    return Fail($"Refusing to overwrite existing file(s): {string.Join(", ", existing)}. Use --overwrite to regenerate.", ExitInvalidArguments, includeUsage: false);
                }
            }

            int written = 0;
            foreach (GeneratedModel model in models)
            {
                File.WriteAllText(Path.Combine(outDir, model.FileName), model.Code);
                written++;
            }

            Console.WriteLine($"Generated {written} file(s) into {outDir} (namespace {ns}).");
            return ExitSuccess;
        }
        catch (DataverseConnectionException ex)
        {
            return FailWithException(ex, ExitConnectionFailure, options.Verbose);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return FailWithException(ex, ExitFileNotFound, options.Verbose);
        }
        catch (Exception ex)
        {
            return FailWithException(ex, ExitParseError, options.Verbose);
        }
    }

    private static List<string> ResolveLogicalNames(IOrganizationService service, string? solution, string[] entities)
    {
        SortedSet<string> names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string entity in entities)
        {
            names.Add(entity);
        }

        if (!string.IsNullOrWhiteSpace(solution))
        {
            foreach (string name in EntitiesInSolution(service, solution!))
            {
                names.Add(name);
            }
        }

        return names.ToList();
    }

    // Derives the table list from a solution's components (componenttype 1 = Entity), mapping the component
    // object ids to logical names via a lightweight RetrieveAllEntities (entity filter only).
    private static IEnumerable<string> EntitiesInSolution(IOrganizationService service, string uniqueName)
    {
        QueryExpression solutionQuery = new QueryExpression("solution") { ColumnSet = new ColumnSet("solutionid"), TopCount = 1 };
        solutionQuery.Criteria.AddCondition("uniquename", ConditionOperator.Equal, uniqueName);
        Entity? solution = service.RetrieveMultiple(solutionQuery).Entities.FirstOrDefault()
            ?? throw new InvalidOperationException($"Solution '{uniqueName}' was not found.");

        QueryExpression componentQuery = new QueryExpression("solutioncomponent") { ColumnSet = new ColumnSet("objectid") };
        componentQuery.Criteria.AddCondition("solutionid", ConditionOperator.Equal, solution.Id);
        componentQuery.Criteria.AddCondition("componenttype", ConditionOperator.Equal, 1);
        HashSet<Guid> objectIds = service.RetrieveAll(componentQuery)
            .Select(e => e.GetAttributeValue<Guid>("objectid"))
            .ToHashSet();

        if (objectIds.Count == 0)
        {
            return Enumerable.Empty<string>();
        }

        RetrieveAllEntitiesResponse all = (RetrieveAllEntitiesResponse)service.Execute(
            new RetrieveAllEntitiesRequest { EntityFilters = EntityFilters.Entity, RetrieveAsIfPublished = true });
        return all.EntityMetadata
            .Where(m => m.MetadataId.HasValue && objectIds.Contains(m.MetadataId.Value) && !string.IsNullOrWhiteSpace(m.LogicalName))
            .Select(m => m.LogicalName)
            .ToList();
    }

    private static EntityMetadata ReadEntity(IOrganizationService service, string logicalName)
    {
        RetrieveEntityResponse response = (RetrieveEntityResponse)service.Execute(
            new RetrieveEntityRequest { LogicalName = logicalName, EntityFilters = EntityFilters.Attributes | EntityFilters.Entity });
        return response.EntityMetadata;
    }

    private static CustomApiMessage ReadCustomApi(IOrganizationService service, string uniqueName)
    {
        QueryExpression query = new QueryExpression("customapi") { ColumnSet = new ColumnSet("customapiid", "bindingtype"), TopCount = 1 };
        query.Criteria.AddCondition("uniquename", ConditionOperator.Equal, uniqueName);
        Entity api = service.RetrieveMultiple(query).Entities.FirstOrDefault()
            ?? throw new InvalidOperationException($"Custom API '{uniqueName}' was not found.");

        bool boundToEntity = (api.GetAttributeValue<OptionSetValue>("bindingtype")?.Value ?? 0) == 1;
        return new CustomApiMessage(
            uniqueName,
            boundToEntity,
            ReadCustomApiParameters(service, "customapirequestparameter", api.Id),
            ReadCustomApiParameters(service, "customapiresponseproperty", api.Id));
    }

    private static List<CustomApiParameter> ReadCustomApiParameters(IOrganizationService service, string entity, Guid customApiId)
    {
        QueryExpression query = new QueryExpression(entity) { ColumnSet = new ColumnSet("uniquename", "type") };
        query.Criteria.AddCondition("customapiid", ConditionOperator.Equal, customApiId);
        return service.RetrieveAll(query)
            .Select(e => new CustomApiParameter(
                e.GetAttributeValue<string>("uniquename") ?? string.Empty,
                e.GetAttributeValue<OptionSetValue>("type")?.Value ?? 10))
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<RoleModel> ReadRoles(IOrganizationService service, string[] roleNames, string? solution)
    {
        // Resolve to unique root roles (id -> name), merging explicit names and the solution's role components so a
        // role listed both ways is generated once.
        Dictionary<Guid, string> roots = new Dictionary<Guid, string>();

        foreach (string name in roleNames)
        {
            Entity root = ReadRootRole(service, name);
            roots[root.Id] = root.GetAttributeValue<string>("name") ?? name;
        }

        if (!string.IsNullOrWhiteSpace(solution))
        {
            foreach (Entity role in RolesInSolution(service, solution!))
            {
                roots[role.Id] = role.GetAttributeValue<string>("name") ?? role.Id.ToString();
            }
        }

        IReadOnlyDictionary<Guid, (string Entity, PrivilegeType Type)> privileges = BuildPrivilegeMap(service);
        return roots
            .OrderBy(r => r.Value, StringComparer.Ordinal)
            .Select(r => new RoleModel(r.Value, r.Key, RolePrivilegeMapper.ToGrants(ReadRolePrivileges(service, r.Key), privileges)))
            .ToList();
    }

    // Every security role in a solution (solutioncomponent componenttype 20). Solution components reference the
    // root role, so the object ids are the stable root ids.
    private static IEnumerable<Entity> RolesInSolution(IOrganizationService service, string uniqueName)
    {
        QueryExpression solutionQuery = new QueryExpression("solution") { ColumnSet = new ColumnSet("solutionid"), TopCount = 1 };
        solutionQuery.Criteria.AddCondition("uniquename", ConditionOperator.Equal, uniqueName);
        Entity solutionRecord = service.RetrieveMultiple(solutionQuery).Entities.FirstOrDefault()
            ?? throw new InvalidOperationException($"Solution '{uniqueName}' was not found.");

        QueryExpression componentQuery = new QueryExpression("solutioncomponent") { ColumnSet = new ColumnSet("objectid") };
        componentQuery.Criteria.AddCondition("solutionid", ConditionOperator.Equal, solutionRecord.Id);
        componentQuery.Criteria.AddCondition("componenttype", ConditionOperator.Equal, 20); // 20 = Role
        List<Guid> roleIds = service.RetrieveAll(componentQuery)
            .Select(e => e.GetAttributeValue<Guid>("objectid"))
            .Distinct()
            .ToList();

        foreach (Guid roleId in roleIds)
        {
            yield return service.Retrieve("role", roleId, new ColumnSet("roleid", "name", "parentrootroleid"));
        }
    }

    // The root (template) role for a name. Roles are duplicated per business unit; the root is the one whose
    // parentrootroleid points at itself, and its id is the stable id plugin logic uses.
    private static Entity ReadRootRole(IOrganizationService service, string name)
    {
        QueryExpression query = new QueryExpression("role") { ColumnSet = new ColumnSet("roleid", "name", "parentrootroleid") };
        query.Criteria.AddCondition("name", ConditionOperator.Equal, name);
        List<Entity> matches = service.RetrieveAll(query).ToList();

        return matches.FirstOrDefault(r => r.GetAttributeValue<EntityReference>("parentrootroleid")?.Id == r.Id)
            ?? matches.FirstOrDefault()
            ?? throw new InvalidOperationException($"Security role '{name}' was not found in the connected environment.");
    }

    private static IEnumerable<(Guid PrivilegeId, int DepthMask)> ReadRolePrivileges(IOrganizationService service, Guid roleId)
    {
        QueryExpression query = new QueryExpression("roleprivileges") { ColumnSet = new ColumnSet("privilegeid", "privilegedepthmask") };
        query.Criteria.AddCondition("roleid", ConditionOperator.Equal, roleId);
        return service.RetrieveAll(query)
            .Select(e => (e.GetAttributeValue<Guid>("privilegeid"), e.GetAttributeValue<int>("privilegedepthmask")))
            .ToList();
    }

    // privilegeId -> (owning table, privilege type), from every table's Privileges metadata. Miscellaneous
    // (non-table) privileges aren't in any table's collection, so they're naturally excluded from generation.
    private static IReadOnlyDictionary<Guid, (string Entity, PrivilegeType Type)> BuildPrivilegeMap(IOrganizationService service)
    {
        RetrieveAllEntitiesResponse response = (RetrieveAllEntitiesResponse)service.Execute(
            new RetrieveAllEntitiesRequest { EntityFilters = EntityFilters.Entity | EntityFilters.Privileges, RetrieveAsIfPublished = true });

        Dictionary<Guid, (string, PrivilegeType)> map = new Dictionary<Guid, (string, PrivilegeType)>();
        foreach (EntityMetadata entity in response.EntityMetadata)
        {
            if (entity.Privileges == null || string.IsNullOrWhiteSpace(entity.LogicalName))
            {
                continue;
            }

            foreach (SecurityPrivilegeMetadata privilege in entity.Privileges)
            {
                map[privilege.PrivilegeId] = (entity.LogicalName, privilege.PrivilegeType);
            }
        }

        return map;
    }

    private static string? ResolveNamespace(string[] args)
    {
        string? explicitNs = TryReadOption(args, "--namespace");
        if (!string.IsNullOrWhiteSpace(explicitNs))
        {
            return explicitNs;
        }

        string? projectFile = ResolveProjectFile(TryReadOption(args, "--project") ?? TryReadOption(args, "-p"));
        if (projectFile == null)
        {
            return null;
        }

        string prefix = StripRoleSuffix(ReadRootNamespace(projectFile));
        return $"{prefix}.Shared.Models.Generated";
    }

    private static string? ResolveProjectFile(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (File.Exists(requested) && requested.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                return requested;
            }

            if (Directory.Exists(requested))
            {
                return SingleCsproj(requested);
            }

            return null;
        }

        return SingleCsproj(Directory.GetCurrentDirectory());
    }

    private static string? SingleCsproj(string directory)
    {
        string[] candidates = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly);
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static void PrintModelsUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  vela models --solution <uniquename> [connection options]");
        Console.WriteLine("  vela models --entities account,contact [connection options]");
        Console.WriteLine("  vela models --custom-apis zava_DoThing [connection options]");
        Console.WriteLine("  vela models --roles \"Salesperson,Sales Manager\" [connection options]");
        Console.WriteLine();
        Console.WriteLine("Generates early-bound classes for the scoped tables/Custom APIs over your Dataverse connection.");
        Console.WriteLine("  --solution <uniquename>   tables taken from the solution's components");
        Console.WriteLine("  --entities <a,b,c>        explicit table logical names (combinable with --solution)");
        Console.WriteLine("  --custom-apis <a,b,c>     Custom API unique names → typed Request/Response message classes");
        Console.WriteLine("  --roles <a,b,c>           security role names → a dependency-light Roles class (root role id + privileges)");
        Console.WriteLine("  --solution-roles          also generate every security role in --solution");
        Console.WriteLine("  --namespace <ns>          output namespace (default: <prefix>.Shared.Models.Generated, inferred from the project)");
        Console.WriteLine("  --out <dir>               output directory (default: current directory)");
        Console.WriteLine("  --no-annotations          omit DataAnnotations ([StringLength]/[Range])");
        Console.WriteLine("  --overwrite               overwrite existing files");
        Console.WriteLine("  connection: --connection-string | --url + --tenant-id + --client-id + --client-secret | --interactive");
    }
}
