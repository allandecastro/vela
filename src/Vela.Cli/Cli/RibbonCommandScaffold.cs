using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Vela.Cli;

/// <summary>
/// Pure helpers for <c>vela add ribbon-script &lt;entity&gt;</c>: the ribbon command / test templates, the web
/// resource naming, the deploy namespace, and resolving the entity's generated form model from the barrel. No I/O -
/// <see cref="AddCommand"/> reads/writes the files. Commands are plain functions wired with <c>formCommand</c> /
/// <c>formRule</c> and attached to a dotted global by <c>bootstrapCommands</c>; the maker points the button's action
/// at <c>&lt;Prefix&gt;.Ribbon.&lt;entity&gt;.&lt;name&gt;</c> in the Command Designer (no appaction is generated).
/// </summary>
internal static class RibbonCommandScaffold
{
    // The web resource name (path minus extension) and its source module, mirroring the Forms convention.
    public static string EntryName(string webResourcePrefix, string entity) =>
        $"{webResourcePrefix}_/js/{entity}/Ribbon/commands";

    public static string SourcePath(string entity) => $"src/{entity}/Ribbon/commands.ts";

    /// <summary>The command-context type alias the generator emits for an entity's Main form (<c>&lt;Entity&gt;Cmd</c>).</summary>
    public static string ContextType(string entity) => FormScriptScaffold.Pascal(entity) + "Cmd";

    // The dotted deploy namespace the maker wires the button to: <Prefix>.Ribbon.<entity>. The prefix is the
    // web-resources project name without its ".WebResources" suffix (e.g. Zava.WebResources -> Zava); it falls back
    // to the web resource prefix when the folder does not follow that convention.
    public static string Namespace(string projectDir, string webResourcePrefix, string entity)
    {
        string folder = new DirectoryInfo(projectDir).Name;
        const string suffix = ".WebResources";
        string prefix = folder.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? folder.Substring(0, folder.Length - suffix.Length)
            : webResourcePrefix;
        return $"{prefix}.Ribbon.{entity}";
    }

    /// <summary>
    /// Resolves the entity's Main form model const from the generated barrel (its exact name depends on the form's
    /// display name, e.g. <c>ContactInformation</c>). Finds the module that re-exports <c>&lt;Entity&gt;Cmd</c>, then
    /// its value export. Returns false with an <paramref name="error"/> when the entity has no generated Main form.
    /// </summary>
    public static bool TryResolveModel(string barrel, string entity, out string modelConst, out string error)
    {
        modelConst = string.Empty;
        error = string.Empty;
        string cmdType = ContextType(entity);

        Match cmd = Regex.Match(barrel ?? string.Empty, $@"export type \{{[^}}]*\b{Regex.Escape(cmdType)}\b[^}}]*\}} from '([^']+)';");
        if (!cmd.Success)
        {
            error = $"No Main form model for '{entity}' in src/generated (expected {cmdType}). Run 'vela models --forms --entities {entity}' first.";
            return false;
        }

        string module = cmd.Groups[1].Value;
        Match value = Regex.Match(barrel!, $@"export \{{\s*([^}}]+?)\s*\}} from '{Regex.Escape(module)}';");
        if (!value.Success)
        {
            error = $"Could not find the form model export for '{entity}' next to {cmdType} in src/generated.";
            return false;
        }

        modelConst = value.Groups[1].Value.Split(',')[0].Trim();
        return true;
    }

    // The command module, wiring an example action + enable rule against the resolved model const and context type.
    public static string Commands(string entity, string ns, string modelConst, string contextType) =>
$$"""
// Ribbon commands for the {{entity}} table (hand-written). A command is a plain function whose parameter is the
// typed command context ({{contextType}}, generated next to the form model); wire it with `formCommand` (action) or
// `formRule` (enable rule, a synchronous boolean). `bootstrapCommands` attaches the wired commands to a dotted
// global so the Command Designer can point a button at `{{ns}}.<name>`.
import { formCommand, formRule, bootstrapCommands } from 'vela-client/ribbon';
import { {{modelConst}} } from '@generated';
import type { {{contextType}} } from '@generated';

// Action: runs when the ribbon button is clicked. `form` is the typed command context.
function myCommand(form: {{contextType}}): void {
  void form;
  // Your command action here.
}

// Enable rule: whether the button is enabled (returns a boolean, synchronously).
function canRun(form: {{contextType}}): boolean {
  void form;
  return true;
}

export const commands = bootstrapCommands(
  { myCommand: formCommand({{modelConst}}, myCommand), canRun: formRule({{modelConst}}, canRun) },
  { namespace: '{{ns}}' },
);
""";

    public static string CommandsTest(string entity, string modelConst) =>
$$"""
import { describe, it, expect } from 'vitest';
import { fakeFormCommand } from 'vela-client/testing';
import { {{modelConst}} } from '@generated';
import { commands } from './commands';

describe('{{entity}} ribbon commands', () => {
  it('the enable rule returns a boolean', () => {
    const form = fakeFormCommand({{modelConst}});
    // The wired rule receives the raw primaryControl; the action would run with commands.myCommand(...).
    expect(typeof commands.canRun(form.primaryControl)).toBe('boolean');
    form.restore();
  });
});
""";
}
