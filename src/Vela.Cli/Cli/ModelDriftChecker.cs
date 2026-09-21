using System;
using System.Collections.Generic;
using System.Linq;

namespace Vela.Cli;

/// <summary>
/// Pure drift check between a locally generated early-bound model and the live Dataverse schema.
/// <para>
/// The generated model is compile-time-safe against the <em>model</em>, not the org: when a live column is
/// deleted or renamed but the model was not regenerated, the code still compiles and typed reads return a
/// silent null at runtime. <c>vela models --verify</c> closes that blind spot by regenerating the model from
/// live metadata in memory and comparing it, attribute by attribute, against the files already on disk.
/// </para>
/// <para>
/// Both sides are reduced to the same shape (per entity: attribute logical name -> C# member type) by
/// <see cref="Extract"/>, so the comparison is independent of formatting, data-annotation options, or member
/// naming - only real schema differences (a column gone, added, or retyped) surface.
/// </para>
/// </summary>
internal static class ModelDriftChecker
{
    /// <summary>An entity class reduced to its logical name and its attribute logical name -> member type map.</summary>
    public sealed record ExtractedEntity(string EntityLogicalName, IReadOnlyDictionary<string, string> Attributes);

    /// <summary>A single attribute whose generated member type differs between the local model and the live schema.</summary>
    public sealed record AttributeTypeChange(string LogicalName, string LocalType, string LiveType);

    /// <summary>Drift for one entity between the on-disk model and the live schema.</summary>
    public sealed record EntityDrift(
        string EntityLogicalName,
        bool MissingLocalFile,
        IReadOnlyList<string> RemovedInDataverse,
        IReadOnlyList<AttributeTypeChange> TypeChanged,
        IReadOnlyList<string> AddedInDataverse)
    {
        /// <summary>True when this entity is out of sync in any way.</summary>
        public bool HasDrift =>
            MissingLocalFile || RemovedInDataverse.Count > 0 || TypeChanged.Count > 0 || AddedInDataverse.Count > 0;
    }

    /// <summary>The full drift report across the verified scope.</summary>
    public sealed record ModelDriftReport(IReadOnlyList<EntityDrift> Entities)
    {
        /// <summary>True when at least one entity in scope is out of sync.</summary>
        public bool HasDrift => Entities.Any(e => e.HasDrift);
    }

    /// <summary>
    /// Reduces one generated C# source file to an <see cref="ExtractedEntity"/>, or <c>null</c> when the file is
    /// not an early-bound entity class (a global option-set file, a support attribute, or anything without the
    /// <c>EntityLogicalNameValue</c> constant the generator emits on every table class).
    /// </summary>
    public static ExtractedEntity? Extract(string source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return null;
        }

        string[] lines = source.Replace("\r\n", "\n").Split('\n');

        string? entityLogicalName = null;
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        string? pendingLogicalName = null;

        foreach (string raw in lines)
        {
            string line = raw.Trim();

            if (entityLogicalName is null)
            {
                string? logical = TryReadEntityLogicalName(line);
                if (logical is not null)
                {
                    entityLogicalName = logical;
                }
            }

            string? attributeLogical = TryReadAttributeLogicalName(line);
            if (attributeLogical is not null)
            {
                // Remember the column; the next property declaration line carries its member type. Facet
                // attributes and data annotations sit in between as their own [..] lines and are skipped.
                pendingLogicalName = attributeLogical;
                continue;
            }

            if (pendingLogicalName is not null && TryReadPropertyType(line, out string memberType))
            {
                // Last write wins is irrelevant: the generator emits one property per column. Keep the first so a
                // stray later match cannot clobber it.
                attributes[pendingLogicalName] = memberType;
                pendingLogicalName = null;
            }
        }

        return entityLogicalName is null ? null : new ExtractedEntity(entityLogicalName, attributes);
    }

    /// <summary>
    /// Compares the freshly generated (live) model sources against the model sources already on disk (local),
    /// entity by entity. Only entities present on the live side (the resolved verify scope) are reported; local
    /// files for out-of-scope entities are ignored.
    /// </summary>
    public static ModelDriftReport Compare(IEnumerable<string> liveSources, IEnumerable<string> localSources)
    {
        Dictionary<string, ExtractedEntity> live = Index(liveSources);
        Dictionary<string, ExtractedEntity> local = Index(localSources);

        var drifts = new List<EntityDrift>();
        foreach (KeyValuePair<string, ExtractedEntity> pair in live.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            string entity = pair.Key;
            ExtractedEntity liveEntity = pair.Value;

            if (!local.TryGetValue(entity, out ExtractedEntity? localEntity))
            {
                drifts.Add(new EntityDrift(entity, MissingLocalFile: true, [], [], []));
                continue;
            }

            var removed = new List<string>();
            var typeChanged = new List<AttributeTypeChange>();
            foreach (KeyValuePair<string, string> attr in localEntity.Attributes.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (!liveEntity.Attributes.TryGetValue(attr.Key, out string? liveType))
                {
                    removed.Add(attr.Key);
                }
                else if (!string.Equals(liveType, attr.Value, StringComparison.Ordinal))
                {
                    typeChanged.Add(new AttributeTypeChange(attr.Key, attr.Value, liveType));
                }
            }

            var added = liveEntity.Attributes.Keys
                .Where(k => !localEntity.Attributes.ContainsKey(k))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

            drifts.Add(new EntityDrift(entity, MissingLocalFile: false, removed, typeChanged, added));
        }

        return new ModelDriftReport(drifts);
    }

    private static Dictionary<string, ExtractedEntity> Index(IEnumerable<string> sources)
    {
        var map = new Dictionary<string, ExtractedEntity>(StringComparer.Ordinal);
        foreach (string source in sources)
        {
            ExtractedEntity? entity = Extract(source);
            if (entity is not null)
            {
                map[entity.EntityLogicalName] = entity;
            }
        }

        return map;
    }

    // public const string EntityLogicalNameValue = "account";
    private static string? TryReadEntityLogicalName(string line)
    {
        const string marker = "EntityLogicalNameValue = \"";
        int start = line.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        int end = line.IndexOf('"', start);
        return end < 0 ? null : line.Substring(start, end - start);
    }

    // [AttributeLogicalName("telephone1")]
    private static string? TryReadAttributeLogicalName(string line)
    {
        const string marker = "[AttributeLogicalName(\"";
        if (!line.StartsWith(marker, StringComparison.Ordinal))
        {
            return null;
        }

        int start = marker.Length;
        int end = line.IndexOf('"', start);
        return end < 0 ? null : line.Substring(start, end - start);
    }

    // public string Name  /  public int? Number  /  public System.Collections.Generic.IList<Foo> Bar
    // The generator emits attribute-backed properties as exactly "public <type> <name>" (the member type never
    // contains a space), and puts the opening brace on the next line. Reject const/ctor/method/modifier lines.
    private static bool TryReadPropertyType(string line, out string memberType)
    {
        memberType = string.Empty;
        if (!line.StartsWith("public ", StringComparison.Ordinal))
        {
            return false;
        }

        // A property declaration line has no body punctuation - a const has '=', a ctor/method/key has '('.
        if (line.IndexOf('=') >= 0 || line.IndexOf('(') >= 0)
        {
            return false;
        }

        string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 3 || tokens[0] != "public")
        {
            // "public override Guid Id" (4 tokens) and "public enum X" are not attribute-backed properties.
            return false;
        }

        memberType = tokens[1];
        return true;
    }
}
