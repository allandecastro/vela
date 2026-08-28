using System;
using System.IO;
using System.Linq;

namespace Vela.Cli;

/// <summary>
/// Pure helpers for <c>vela add command &lt;entity&gt;</c>: the ribbon command / test templates, the web resource
/// naming, and the deploy namespace. No I/O - <see cref="AddCommand"/> reads/writes the files. Commands are plain
/// functions wired with <c>formCommand</c> / <c>formRule</c> and attached to a dotted global by
/// <c>bootstrapCommands</c>; the maker points the button's action at <c>&lt;Prefix&gt;.Ribbon.&lt;entity&gt;.&lt;name&gt;</c>
/// in the Command Designer (no appaction is generated).
/// </summary>
internal static class RibbonCommandScaffold
{
    // The web resource name (path minus extension) and its source module, mirroring the Forms convention.
    public static string EntryName(string webResourcePrefix, string entity) =>
        $"{webResourcePrefix}_/js/{entity}/Ribbon/commands";

    public static string SourcePath(string entity) => $"src/{entity}/Ribbon/commands.ts";

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

    public static string Commands(string entity, string ns)
    {
        string pascal = FormScriptScaffold.Pascal(entity);
        return
$$"""
// Ribbon commands for the {{entity}} table (hand-written). A command is a plain function whose parameter is the
// typed command context (<Entity>Cmd, from the generated model); wire it with `formCommand` (action) or `formRule`
// (enable rule, a synchronous boolean). `bootstrapCommands` attaches the wired commands to a dotted global so the
// Command Designer can point a button at `{{ns}}.<name>`.
import { formCommand, formRule, bootstrapCommands } from 'vela-client/ribbon';
import { {{pascal}}Main } from '@generated';
import type { {{pascal}}Cmd } from '@generated';

// Action: runs when the ribbon button is clicked. `form` is the typed command context.
function myCommand(form: {{pascal}}Cmd): void {
  void form;
  // Your command action here.
}

// Enable rule: whether the button is enabled (returns a boolean, synchronously).
function canRun(form: {{pascal}}Cmd): boolean {
  void form;
  return true;
}

export const commands = bootstrapCommands(
  { myCommand: formCommand({{pascal}}Main, myCommand), canRun: formRule({{pascal}}Main, canRun) },
  { namespace: '{{ns}}' },
);
""";
    }

    public static string CommandsTest(string entity)
    {
        string pascal = FormScriptScaffold.Pascal(entity);
        return
$$"""
import { describe, it, expect } from 'vitest';
import { fakeFormCommand } from 'vela-client/testing';
import { {{pascal}}Main } from '@generated';
import { commands } from './commands';

describe('{{entity}} ribbon commands', () => {
  it('the enable rule returns a boolean', () => {
    const form = fakeFormCommand({{pascal}}Main);
    // The wired rule receives the raw primaryControl; the action would run with commands.myCommand(...).
    expect(typeof commands.canRun(form.primaryControl)).toBe('boolean');
    form.restore();
  });
});
""";
    }
}
