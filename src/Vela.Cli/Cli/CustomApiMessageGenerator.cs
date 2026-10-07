using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Vela.Cli;

/// <summary>
/// A Custom API parameter/property: its name and Dataverse <c>customapifieldtype</c> value. <paramref name="IsOptional"/>
/// only applies to request parameters (Dataverse <c>customapirequestparameter.isoptional</c>); response properties are
/// always present, so it stays false for them. When true it suppresses the generated <c>[Required]</c> attribute so a
/// round-trip (generate the model, rebuild the manifest, re-apply) preserves the parameter's optionality.
/// </summary>
public sealed record CustomApiParameter(string Name, int FieldType, bool IsOptional = false);

/// <summary>
/// A Custom API message to generate typed Request/Response classes for. <paramref name="AllowedStepType"/>,
/// <paramref name="IsPrivate"/> and <paramref name="BoundEntityLogicalName"/> drive the Phase 3c compile-time
/// registrability of the generated <c>On&lt;Message&gt;()</c> step extension (which stages/modes it offers, which
/// entity it applies to, or whether it is emitted at all). They default to a permissive, global shape so callers
/// that do not care keep working.
/// </summary>
/// <param name="UniqueName">The Custom API unique name (its message name).</param>
/// <param name="IsBoundToEntity">Whether the Custom API is bound to an entity (vs a global/unbound API).</param>
/// <param name="RequestParameters">The input (request) parameters.</param>
/// <param name="ResponseProperties">The output (response) properties.</param>
/// <param name="AllowedStepType">Dataverse <c>allowedcustomprocessingsteptype</c>: 0 None, 1 AsyncOnly, 2 SyncAndAsync.</param>
/// <param name="IsPrivate">Dataverse <c>isprivate</c>: a private Custom API is not meant to be extended by steps.</param>
/// <param name="BoundEntityLogicalName">Dataverse <c>boundentitylogicalname</c> (null/empty for a global API); when
/// set, the generated step extension is constrained to that entity for compile-time applicability.</param>
public sealed record CustomApiMessage(
    string UniqueName,
    bool IsBoundToEntity,
    IReadOnlyList<CustomApiParameter> RequestParameters,
    IReadOnlyList<CustomApiParameter> ResponseProperties,
    int AllowedStepType = 2,
    bool IsPrivate = false,
    string? BoundEntityLogicalName = null);

/// <summary>
/// Pure generator for Custom API message classes: a typed <c>Vela.Messages.TypedRequest</c> (one read-write
/// property per request parameter, over <c>Parameters</c>) and a typed <c>Vela.Messages.TypedResponse</c> (one
/// read-only property per output, over <c>Results</c>). Each property is a one-liner over the base
/// <c>Get&lt;T&gt;()</c> / <c>Set(value)</c> helpers. Wrap a base response returned by <c>IOrganizationService</c>
/// with <c>service.Execute(new XxxRequest { ... }).ToResponse&lt;XxxResponse&gt;()</c>.
/// </summary>
public static class CustomApiMessageGenerator
{
    /// <param name="messages">The Custom API messages to generate typed request/response classes for.</param>
    /// <param name="ns">The target namespace for the generated files.</param>
    /// <param name="entityTypeNames">Optional map from an entity logical name to its early-bound C# type name (its
    /// SchemaName). Used so a bound Custom API's <c>On&lt;Message&gt;()</c> extension references the exact generated
    /// type; falls back to PascalCase when null or missing.</param>
    /// <param name="generatedEntityLogicalNames">Optional set of the entity logical names actually generated in this
    /// batch. When provided, a bound Custom API whose bound entity is NOT in this set degrades to the unbound generic
    /// extension (referencing the absent early-bound type would not compile, CS0246). When null, every bound entity is
    /// assumed to be present (the pre-existing behavior).</param>
    public static IReadOnlyList<GeneratedModel> Generate(IEnumerable<CustomApiMessage> messages, string ns, IReadOnlyDictionary<string, string>? entityTypeNames = null, IReadOnlyCollection<string>? generatedEntityLogicalNames = null)
    {
        if (messages == null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        if (string.IsNullOrWhiteSpace(ns))
        {
            throw new ArgumentException("A target namespace is required.", nameof(ns));
        }

        HashSet<string>? generatedEntities = generatedEntityLogicalNames == null
            ? null
            : new HashSet<string>(generatedEntityLogicalNames, StringComparer.OrdinalIgnoreCase);

        List<CustomApiMessage> ordered = messages
            .Where(m => !string.IsNullOrWhiteSpace(m?.UniqueName))
            .OrderBy(m => m.UniqueName, StringComparer.Ordinal)
            .ToList();

        // Two Custom APIs from different publisher prefixes (zava_Calc, acme_Calc) would both produce an OnCalc(...)
        // extension method and collide (CS0121). Detect the suffixes that appear more than once so the generator can
        // disambiguate those with the full, prefixed name (OnZavaCalc / OnAcmeCalc) while keeping the short name for
        // the common, non-colliding case.
        HashSet<string> collidingSuffixes = ordered
            .GroupBy(m => MethodSuffix(m.UniqueName), StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        return ordered
            .Select(m => new GeneratedModel($"Messages/{m.UniqueName}.cs", GenerateOne(m, ns, collidingSuffixes, entityTypeNames, generatedEntities)))
            .ToList();
    }

    private static string GenerateOne(CustomApiMessage message, string ns, HashSet<string> collidingSuffixes, IReadOnlyDictionary<string, string>? entityTypeNames, HashSet<string>? generatedEntities)
    {
        string request = $"{message.UniqueName}Request";
        string response = $"{message.UniqueName}Response";

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine($"namespace {ns}");
        sb.AppendLine("{");
        sb.AppendLine("    using System;");
        sb.AppendLine("    using Microsoft.Xrm.Sdk;");
        sb.AppendLine();

        // Request. Derives from CustomApiRequest, which keys the Parameters bag by the plain property name (the
        // Custom API's request parameters carry bare-identifier uniquenames equal to their plain names).
        sb.AppendLine($"    /// <summary>Typed request for the <c>{message.UniqueName}</c> Custom API.</summary>");
        sb.AppendLine($"    public partial class {request} : Vela.Messages.CustomApiRequest");
        sb.AppendLine("    {");
        sb.AppendLine($"        public {request}() : base(\"{message.UniqueName}\")");
        sb.AppendLine("        {");
        sb.AppendLine("        }");

        // Track the C# member names already emitted on the request so a parameter cannot duplicate one (CS0102).
        HashSet<string> usedRequestMembers = new HashSet<string>(StringComparer.Ordinal);

        if (message.IsBoundToEntity)
        {
            // The bound record is passed under the standard, unprefixed "Target" key (not "{api}.Target"),
            // so it bypasses the CustomApiRequest prefixing and reads Parameters["Target"] directly.
            sb.AppendLine();
            sb.AppendLine("        /// <summary>The bound record (Parameters[\"Target\"]).</summary>");
            sb.AppendLine("        public EntityReference Target");
            sb.AppendLine("        {");
            sb.AppendLine("            get => Parameters.Contains(\"Target\") && Parameters[\"Target\"] is EntityReference reference ? reference : null;");
            sb.AppendLine("            set => Parameters[\"Target\"] = value;");
            sb.AppendLine("        }");
            usedRequestMembers.Add("Target");
        }

        foreach (CustomApiParameter parameter in message.RequestParameters)
        {
            (string member, _) = CSharpIdentifier.Member(parameter.Name);
            if (!usedRequestMembers.Add(member))
            {
                // A request parameter whose C# member name collides with an already-emitted member (most commonly a
                // bound API's auto 'Target' property) would be CS0102. That member already exposes the same
                // Parameters slot, so skip the duplicate rather than emit non-compiling code. (#308)
                sb.AppendLine();
                sb.AppendLine($"        // Request parameter '{parameter.Name}' shares the '{member}' member emitted above.");
                continue;
            }

            AppendParameter(sb, parameter, readOnly: false);
        }

        sb.AppendLine("    }");
        sb.AppendLine();

        // Response. Derives from CustomApiResponse, which keys the Results bag by the plain property name (the
        // Custom API's response properties carry bare-identifier uniquenames equal to their plain names).
        sb.AppendLine($"    /// <summary>Typed response for the <c>{message.UniqueName}</c> Custom API.</summary>");
        sb.AppendLine($"    public partial class {response} : Vela.Messages.CustomApiResponse");
        sb.AppendLine("    {");
        sb.AppendLine($"        public {response}() : base(\"{message.UniqueName}\")");
        sb.AppendLine("        {");
        sb.AppendLine("        }");

        foreach (CustomApiParameter property in message.ResponseProperties)
        {
            AppendParameter(sb, property, readOnly: true);
        }

        sb.AppendLine("    }");

        AppendStepExtensions(sb, message, collidingSuffixes, entityTypeNames, generatedEntities);

        sb.AppendLine("}");
        return sb.ToString();
    }

    // Phase 3c: emit a registrability-gated On<Message>() step extension for the Custom API. A bound API (boundentitylogicalname set) constrains the
    // extension to Vela.Steps.IStepTrigger<BoundEntity> for compile-time entity applicability; a global one emits a generic
    // On<Message><TEntity>(). An AsyncOnly API (allowedcustomprocessingsteptype == 1) returns an async-only stage
    // (no .Sync()); a None (0) or private API emits no extension at all (only a comment), so the blessed path does
    // not compile - the generic On<XxxRequest>() escape hatch stays for explicit use, and Dataverse rejects an
    // invalid registration at deploy time regardless. The referenced early-bound entity type is assumed to be
    // generated into this same namespace.
    private static void AppendStepExtensions(StringBuilder sb, CustomApiMessage message, HashSet<string> collidingSuffixes, IReadOnlyDictionary<string, string>? entityTypeNames, HashSet<string>? generatedEntities)
    {
        string request = $"{message.UniqueName}Request";
        // Short name (publisher prefix stripped) unless it would collide with another API's; then use the full,
        // prefixed name to keep the extension methods unambiguous (A18).
        string suffix = MethodSuffix(message.UniqueName);
        string methodBase = collidingSuffixes.Contains(suffix) ? PascalCase(message.UniqueName) : suffix;

        // The request class name ({UniqueName}Request) must be a valid identifier to be referenced at all.
        if (!IsValidIdentifier(message.UniqueName))
        {
            sb.AppendLine();
            sb.AppendLine($"    // No step extension: Custom API '{message.UniqueName}' is not a valid C# identifier.");
            return;
        }

        // None or private: no blessed On<Message>() extension (use On<{UniqueName}Request>() explicitly if intended).
        if (message.IsPrivate || message.AllowedStepType == 0)
        {
            string reason = message.IsPrivate ? "is private" : "allows no processing step";
            sb.AppendLine();
            sb.AppendLine($"    // No On{methodBase}() step extension: Custom API '{message.UniqueName}' {reason}. Use On<{request}>() explicitly if you really intend to register a step.");
            return;
        }

        bool asyncOnly = message.AllowedStepType == 1;
        string stageType = asyncOnly
            ? $"Vela.Steps.IAsyncOnlyTypedStages"
            : $"Vela.Steps.ITypedStages";
        string entry = asyncOnly ? $"OnAsyncOnly<{request}>" : $"On<{request}>";
        string method = $"On{methodBase}";
        bool bound = !string.IsNullOrWhiteSpace(message.BoundEntityLogicalName);

        // A bound API whose bound entity is not part of this generation batch cannot reference the early-bound type
        // (it is not generated -> CS0246). Fall back to the unbound generic form so the output still compiles.
        bool boundEntityGenerated = bound
            && (generatedEntities == null || generatedEntities.Contains(message.BoundEntityLogicalName!));

        sb.AppendLine();
        sb.AppendLine($"    /// <summary>Registrability-gated <c>{method}()</c> step extension for the <c>{message.UniqueName}</c> Custom API (Phase 3c: compile-time entity applicability{(asyncOnly ? "; async-only, no .Sync()" : string.Empty)}). The referenced early-bound entity type is assumed to live in this same namespace.</summary>");
        sb.AppendLine($"    public static class {message.UniqueName}StepExtensions");
        sb.AppendLine("    {");

        if (boundEntityGenerated)
        {
            string logical = message.BoundEntityLogicalName!;
            string pascal = entityTypeNames != null && entityTypeNames.TryGetValue(logical, out string? typeName) && !string.IsNullOrEmpty(typeName)
                ? typeName!
                : PascalCase(logical);
            sb.AppendLine($"        public static {stageType}<{pascal}, {request}> {method}(this Vela.Steps.IStepTrigger<{pascal}> builder) => builder.{entry}();");
        }
        else
        {
            if (bound)
            {
                // Bound entity absent from this batch: keep a usable (generic) extension instead of a dangling type
                // reference. Regenerate the models including '{BoundEntityLogicalName}' to get the entity-constrained overload.
                sb.AppendLine($"        // Bound entity '{message.BoundEntityLogicalName}' is not in this model batch; emitting the unbound generic form (regenerate that entity for the entity-constrained {method}() overload).");
            }

            sb.AppendLine($"        public static {stageType}<TEntity, {request}> {method}<TEntity>(this Vela.Steps.IStepTrigger<TEntity> builder) where TEntity : Microsoft.Xrm.Sdk.Entity => builder.{entry}();");
        }

        sb.AppendLine("    }");
    }

    // The On<Message>() method-name suffix: the Custom API unique name minus its publisher prefix (the text up to
    // and including the first '_'), PascalCased. E.g. "zava_CalculateAccountScore" -> "CalculateAccountScore",
    // "acme_recalc_score" -> "RecalcScore". A name with no '_' is used whole.
    private static string MethodSuffix(string uniqueName)
    {
        int underscore = uniqueName.IndexOf('_');
        string withoutPrefix = underscore >= 0 && underscore < uniqueName.Length - 1
            ? uniqueName.Substring(underscore + 1)
            : uniqueName;
        return PascalCase(withoutPrefix);
    }

    // PascalCase for referencing an early-bound entity type / building a method name: split on '_', capitalise the
    // first letter of each part, keep the rest (e.g. "lead" -> "Lead", "new_customentity" -> "NewCustomentity").
    private static string PascalCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value ?? string.Empty;
        }

        return string.Concat(value
            .Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part.Substring(1)));
    }

    private static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name) || !(char.IsLetter(name[0]) || name[0] == '_'))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static void AppendParameter(StringBuilder sb, CustomApiParameter parameter, bool readOnly)
    {
        string type = MapType(parameter.FieldType);
        (string member, string key) = CSharpIdentifier.Member(parameter.Name);
        string setArgs = key.Length > 0 ? "value, " + key : "value";
        sb.AppendLine();
        if (readOnly)
        {
            sb.AppendLine($"        public {type} {member} => Get<{type}>({key});");
        }
        else
        {
            // A required request parameter (isoptional == false in Dataverse) is emitted with [Required] so that,
            // when this generated model is used to rebuild the manifest, the reflection-based parameter reader marks
            // it required again - without it, a regenerated model would silently make every parameter optional.
            if (!parameter.IsOptional)
            {
                sb.AppendLine("        [System.ComponentModel.DataAnnotations.Required]");
            }

            sb.AppendLine($"        public {type} {member}");
            sb.AppendLine("        {");
            sb.AppendLine($"            get => Get<{type}>({key});");
            sb.AppendLine($"            set => Set({setArgs});");
            sb.AppendLine("        }");
        }
    }

    // Dataverse customapifieldtype option values → C# types.
    private static string MapType(int fieldType) => fieldType switch
    {
        0 => "bool",
        1 => "DateTime",
        2 => "decimal",
        3 => "Entity",
        4 => "EntityCollection",
        5 => "EntityReference",
        6 => "double",
        7 => "int",
        8 => "Money",
        9 => "OptionSetValue",
        10 => "string",
        11 => "string[]",
        12 => "Guid",
        _ => "object",
    };
}
