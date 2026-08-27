using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Vela.Cli;

/// <summary>
/// Pure helpers for <c>vela add form-script &lt;entity&gt;</c>: the form-script / co-located test templates, the
/// web resource naming convention, and the idempotent insertion of an entry into <c>web-resources.entries.ts</c>.
/// No I/O - <see cref="AddCommand"/> reads/writes the files; this class only produces strings, so it stays
/// unit-testable.
/// </summary>
internal static class FormScriptScaffold
{
    // A Dataverse table logical name: lower-case, starts with a letter, letters/digits/underscore only.
    private static readonly Regex LogicalNamePattern = new("^[a-z][a-z0-9_]*$", RegexOptions.Compiled);

    private static readonly Regex GlobalNamePattern = new(@"globalName:\s*'([^']+)'", RegexOptions.Compiled);

    public static bool IsValidEntity(string entity) =>
        !string.IsNullOrWhiteSpace(entity) && LogicalNamePattern.IsMatch(entity);

    // The web resource name (path minus extension) and its source module - the source path mirrors the deployed
    // path so a reader can map one to the other. `<entity>` is the lower-case logical name in both.
    public static string EntryName(string webResourcePrefix, string entity) =>
        $"{webResourcePrefix}_/js/{entity}/Forms/main";

    public static string SourcePath(string entity) => $"src/{entity}/Forms/main.ts";

    // PascalCase for the referenced form-model const (`<Pascal>Main`), splitting on the logical-name separators the
    // FormModel generator uses, so `new_account` -> `NewAccount` matches the emitted `NewAccountMain`.
    public static string Pascal(string entity)
    {
        string[] words = entity.Split(new[] { '_', '-', '.', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(words.Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1)));
    }

    // The web resource prefix (`adc` from `adc_/js/...`) read from tsup.config.ts's globalName (its first dotted
    // segment). Robust even when the entry map is empty. Returns null when globalName can't be found.
    public static string? WebResourcePrefixFromTsup(string tsupConfig)
    {
        Match match = GlobalNamePattern.Match(tsupConfig ?? string.Empty);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups[1].Value.Split('.')[0];
    }

    // The form-script stub written to src/<entity>/Forms/main.ts. Exports plain onLoad/onSave so the bundle builds
    // and the entries guard passes before a model is generated; the `defineForm` wiring is shown in the comment.
    public static string FormScript(string entity)
    {
        string pascal = Pascal(entity);
        return
$$"""
// Form script for the {{entity}} table (hand-written). Generate the form model with
//   vela models --forms --solution <name> --out src/generated
// then wire the handlers with `defineForm` from vela-client, e.g.
//   import { {{pascal}}Main } from '../../generated/forms/{{entity}}/main.form';
//   export const { onLoad, onSave } = defineForm({{pascal}}Main, class extends VelaForm({{pascal}}Main) {});
// The stubs below keep the bundle building - and this entry exporting onLoad/onSave - until then.

export function onLoad(): void {
  // Your onLoad logic here.
}

export function onSave(): void {
  // Your onSave logic here.
}
""";
    }

    // The co-located Vitest stub written next to the form script (src/<entity>/Forms/main.test.ts).
    public static string FormScriptTest(string entity) =>
$$"""
import { describe, it, expect } from 'vitest';
import { onLoad } from './main';

describe('{{entity}} form', () => {
  it('runs onLoad without throwing', () => {
    expect(() => onLoad()).not.toThrow();
  });
});
""";

    /// <summary>
    /// Inserts <c>'name': 'source',</c> as the last entry of the <c>export const entries = { ... } as const;</c>
    /// map. Idempotent: if the name is already present, <paramref name="updated"/> equals the input and
    /// <paramref name="inserted"/> is false. Returns false only when the map's closing <c>} as const</c> can't be
    /// found (a hand-mangled file).
    /// </summary>
    public static bool TryInsertEntry(string entriesFile, string name, string source, out string updated, out bool inserted)
    {
        updated = entriesFile ?? string.Empty;
        inserted = false;

        // Already mapped (match the exact quoted key) - nothing to do, still a success.
        if (updated.Contains($"'{name}'", StringComparison.Ordinal))
        {
            return true;
        }

        const string closing = "} as const";
        int index = updated.IndexOf(closing, StringComparison.Ordinal);
        if (index < 0)
        {
            return false;
        }

        string entryLine = $"  '{name}': '{source}',\n";
        updated = updated.Insert(index, entryLine);
        inserted = true;
        return true;
    }
}
