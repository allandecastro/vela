using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Vela.Cli;

/// <summary>
/// Pure file-content generators for the <c>vela init</c> command. Every method returns the text of a
/// single generated file and performs no I/O, which keeps templates independently unit-testable.
/// Raw interpolated string literals (<c>$$"""..."""</c>) are used so the emitted C# braces stay
/// literal and only <c>{{token}}</c> placeholders interpolate.
/// </summary>
internal static class ScaffoldTemplates
{
    // Version of the Power Platform plugin MSBuild SDK package. Bump as new releases ship; this only
    // affects newly generated solutions, not this repository.
    public const string PluginSdkVersion = "1.41.1";

    // Cross-platform Dataverse client used by the Shared (netstandard2.0) and ConsoleApp projects.
    public const string DataverseClientVersion = "1.2.10";

    // The Dataverse client pulls System.Security.Cryptography.Xml in transitively at a version with known
    // advisories (NU1903); pin the patched build in the projects that reference the client (Shared, ConsoleApp).
    public const string CryptographyXmlVersion = "8.0.4";

    // .NET Framework Dataverse SDK reference assemblies for plugin (net462) compilation.
    public const string CrmCoreAssembliesVersion = "9.0.2.59";

    // Test stack for the generated *.Plugins.Tests project. These (and Vela.Plugins / Vela.Testing) resolve
    // from nuget.org, the default package source; the scaffold emits no nuget.config.
    public const string TestSdkVersion = "17.12.0";
    public const string XunitVersion = "2.9.2";
    public const string XunitRunnerVersion = "2.8.2";

    private const string CSharpProjectTypeGuid = "FAE04EC0-301F-11D3-BF4B-00C04F79EFBC";

    /// <summary>The Visual Studio JavaScript/TypeScript project system (.esproj) type id.</summary>
    public const string JavaScriptProjectTypeGuid = "54A90642-561A-4BB1-A94E-469ADEE60C69";

    /// <summary>A project to be listed in the generated solution file. The type id defaults to C#.</summary>
    public sealed record ProjectEntry(string Name, string RelativePath, Guid ProjectGuid, string ProjectTypeGuid = CSharpProjectTypeGuid);

    // The Custom API unique-name publisher prefix is derived from the namespace prefix (lower-cased,
    // dots removed) so it is a valid Dataverse customization prefix.
    public static string ApiPrefix(string prefix) =>
        prefix.Replace(".", string.Empty).ToLowerInvariant();

    // ---- Web resources project (opt-in TypeScript) ------------------------
    // npm dependency versions for the scaffolded {prefix}.WebResources project. Pinned as caret ranges so a
    // fresh `npm install` picks up compatible patches.
    public const string TsupVersion = "^8.3.5";
    public const string TypeScriptVersion = "^5.6.3";
    public const string VitestVersion = "^2.1.8";
    public const string VelaClientVersion = "^0.1.6";

    // Node type definitions: the entries guard imports node:url / node:path, so a `tsc --noEmit` typecheck needs
    // these even though esbuild (tsup/vitest) resolves the node: scheme at runtime without them.
    public const string TypesNodeVersion = "^20.14.0";

    // The npm package name for the web resources project: lower-case, dot-free, dash-suffixed.
    public static string WebResourcesPackageName(string prefix) => $"{ApiPrefix(prefix)}-webresources";

    // The web resource NAME prefix (the real top-level folder, e.g. `adc_`) is the first segment of the JS global
    // name. It is distinct from the JS global namespace itself (see the web-resource-project plan).
    private static string WebResourcePrefix(string globalName) => globalName.Split('.')[0];

    public static string WebResourcesPackageJson(string prefix) =>
$$"""
{
  "name": "{{WebResourcesPackageName(prefix)}}",
  "private": true,
  "version": "1.0.0",
  "type": "module",
  "description": "Model-driven form web resources for {{prefix}}, bundled with tsup.",
  "scripts": {
    "build": "tsup",
    "dev": "tsup --watch",
    "test": "vitest run",
    "test:watch": "vitest"
  },
  "dependencies": {
    "vela-client": "{{VelaClientVersion}}"
  },
  "devDependencies": {
    "@types/node": "{{TypesNodeVersion}}",
    "tsup": "{{TsupVersion}}",
    "typescript": "{{TypeScriptVersion}}",
    "vitest": "{{VitestVersion}}"
  }
}
""";

    public static string WebResourcesTsconfig() =>
$$"""
{
  "compilerOptions": {
    "target": "ES2020",
    "module": "ESNext",
    "moduleResolution": "Bundler",
    "strict": true,
    "esModuleInterop": true,
    "skipLibCheck": true,
    "forceConsistentCasingInFileNames": true,
    "types": ["vitest/globals", "node"],
    "baseUrl": ".",
    "paths": {
      "@generated/*": ["src/generated/*"],
      "@generated": ["src/generated"]
    }
  },
  "include": ["src", "test"]
}
""";

    // globalName is the consumer's own publisher prefix (never "Vela"). The output tree mirrors the Dataverse web
    // resource names, so tsup emits one IIFE per form under `<folder>/<prefix>_/js/...`. The entry map lives in
    // web-resources.entries.ts (maintained by `vela add form-script`) so the config itself never has to be edited.
    public static string WebResourcesTsupConfig(string globalName, string folder) =>
$$"""
import { defineConfig } from 'tsup';
import { entries } from './web-resources.entries';

// `globalName` is the IIFE wrapper root (your publisher prefix, lower-case). The handlers a form/ribbon web
// resource actually exposes are set by `defineForm`/`bootstrapCommands` (e.g. `<prefix>.Forms.account.onLoad`,
// `<prefix>.Ribbon.account.<command>`). The entry map (Dataverse web resource name -> source module) lives in
// web-resources.entries.ts - `vela add form-script <entity>` adds to it. One IIFE bundle is emitted per entry.
export default defineConfig({
  entry: entries,
  format: ['iife'],
  globalName: '{{globalName}}',
  outDir: '{{folder}}',
  clean: true,
  minify: true,
  sourcemap: true,
  // Name the bundle `<entry>.js`. Without this tsup suffixes the iife format as `<entry>.global.js`, and the
  // `.global` would leak into the deployed web resource name.
  outExtension() {
    return { js: '.js' };
  },
});
""";

    // The single source of truth for what gets bundled and deployed: Dataverse web resource name (the path, minus
    // extension) -> its source module. `vela add form-script <entity>` inserts here; tsup.config.ts and the entries
    // guard both read it. The web resource name is `<prefix>_/js/<entity>/Forms/main`, mirrored by the source path.
    public static string WebResourcesEntries(string globalName)
    {
        string wrPrefix = WebResourcePrefix(globalName);
        return
$$"""
// Web resource entries: Dataverse web resource name (path, minus extension) -> its source module.
// Maintained by `vela add form-script <entity>`; safe to edit by hand. Keys mirror the source paths.
export const entries = {
  '{{wrPrefix}}_/js/account/Forms/main': 'src/account/Forms/main.ts',
} as const;
""";
    }

    // The web-resources project README: what it is, how to build/test/deploy, and how to add form scripts and
    // ribbon commands. `prefix` is the solution prefix (e.g. `zava.sales`); it is also the ribbon deploy namespace
    // prefix, since the project folder is `<prefix>.WebResources`.
    public static string WebResourcesReadme(string prefix, string globalName, string folder)
    {
        string wrPrefix = WebResourcePrefix(globalName);
        string nsPrefix = prefix.ToLowerInvariant();
        return
$$"""
# {{prefix}}.WebResources

Client-side web resources for **{{prefix}}**, written in TypeScript and bundled with tsup. One IIFE bundle is
emitted per entry in `web-resources.entries.ts`; typed models generated by `vela models --forms --tables` live
under `src/generated` (imported through the `@generated` alias).

## Build & test

```powershell
npm install
npm run build   # bundle every entry into ./{{folder}}
npm test        # vitest: form-script + ribbon tests, plus the entries guard
```

## Form scripts

```powershell
vela add form-script <entity>   # src/<entity>/Forms/main.ts (+ a co-located test), registers the entry
```

A form script is wired with `defineForm(Model, Class, { namespace: '{{nsPrefix}}.Forms.<entity>' })`; on the form's
event handlers, use the library `{{wrPrefix}}_/js/<entity>/Forms/main` and the function name
`{{nsPrefix}}.Forms.<entity>.onLoad` / `.onSave` (aligned with the ribbon's `{{nsPrefix}}.Ribbon.<entity>.<command>`).

## Ribbon commands

```powershell
vela add ribbon-script <entity>   # src/<entity>/Ribbon/commands.ts (+ a co-located test), registers the entry
```

A ribbon command is a plain function wired with `formCommand` (action) / `formRule` (enable rule) and attached
to a dotted global by `bootstrapCommands`. In the Command Designer, point the button's action at
`{{nsPrefix}}.Ribbon.<entity>.<command>`. The entity needs a generated Main form model, so run
`vela models --forms --entities <entity>` first.

## Deploy

```powershell
vela apply --webresources ./{{folder}} --solution <name> --interactive
```
""";
    }

    // A minimal .esproj so Visual Studio's "Build" runs npm, sitting next to the plugin projects in the solution.
    public static string WebResourcesEsproj() =>
$$"""
<Project Sdk="Microsoft.VisualStudio.JavaScript.Sdk/1.0.1184077">
  <PropertyGroup>
    <BuildCommand>npm run build</BuildCommand>
    <StartupCommand>npm run dev</StartupCommand>
    <ShouldRunNpmInstall>true</ShouldRunNpmInstall>
  </PropertyGroup>
</Project>
""";

    public static string WebResourcesVitestConfig() =>
$$"""
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  // Resolve the @generated alias (tsconfig paths) so tests import the barrel the same way as the source.
  resolve: {
    alias: {
      '@generated': fileURLToPath(new URL('./src/generated', import.meta.url)),
    },
  },
  test: {
    globals: true,
    environment: 'node',
    // Co-located form-script tests (src/<entity>/Forms/main.test.ts) plus the shared guards under test/.
    include: ['src/**/*.test.ts', 'test/**/*.test.ts'],
  },
});
""";

    // node_modules and the build output folder are never committed.
    public static string WebResourcesGitignore(string folder) =>
$$"""
node_modules/
{{folder}}/
*.tsbuildinfo
""";

    // A working example form script at src/account/Forms/main.ts: typed field accessors, a typed onChange rule, a
    // typed Web API query (reading the polymorphic lookup annotation), and a save guard - wired with `defineForm`.
    // It compiles and its Vitest passes out of the box against the example models under src/generated. `defineForm`'s
    // namespace exposes the handlers at `<prefix>.Forms.account.onLoad` (aligned with the ribbon's
    // `<prefix>.Ribbon.<entity>.<command>`); the file is `$webresource:<prefix>_/js/account/Forms/main.js`.
    public static string WebResourcesExampleForm(string prefix)
    {
        string ns = prefix.ToLowerInvariant();
        return
$$"""
// Example form script: typed accessors + a typed Web API query, wired with defineForm.
import { VelaForm, defineForm, type SaveEventArgs } from 'vela-client';
import { createWebApi } from 'vela-client/webapi';
import { AccountMain, Contact, account_customertypecode } from '@generated';

export class AccountForm extends VelaForm(AccountMain) {
  onLoad(): void {
    this.fields.customertypecode.addOnChange(this.applyRules);
    this.applyRules();
  }

  applyRules(): void {
    const isCompetitor =
      this.fields.customertypecode.getValue() === account_customertypecode.Competitor;
    this.tabs.details.setVisible(!isCompetitor);
    this.fields.industrycode.setRequiredLevel(isCompetitor ? 'none' : 'required');
  }

  // Typed query: Contact ref infers the row type; the polymorphic lookup target is typed too.
  async relatedContacts(): Promise<Array<{ name: string | null; parentType: string | null }>> {
    if (!this.id) return [];
    const { entities } = await createWebApi()
      .retrieveMultiple(Contact)
      .select('fullname', '_parentcustomerid_value')
      .filter((f) => f.eqGuid('_parentcustomerid_value', this.id))
      .execute();

    return entities.map((c) => ({
      name: c.fullname ?? null,
      parentType: c['_parentcustomerid_value@Microsoft.Dynamics.CRM.lookuplogicalname'] ?? null,
    }));
  }

  onSave(e: SaveEventArgs): void {
    if (!this.fields.name.getValue()) {
      e.preventDefault();
      this.setFormNotification('Account name is required', 'ERROR');
    }
  }
}

// `namespace` exposes the handlers at `{{ns}}.Forms.account.onLoad` / onSave (aligned with the ribbon
// namespace), never "Vela". The onLoad/onSave exports are kept for the entries guard and unit tests.
export const { onLoad, onSave } = defineForm(AccountMain, AccountForm, { namespace: '{{ns}}.Forms.account' });
""";
    }

    // The co-located Vitest example next to the example form script (src/account/Forms/main.test.ts): exercises the
    // onChange rules and the save guard with the `fakeForm` harness, no browser or live Dataverse.
    public static string WebResourcesExampleFormTest() =>
"""
import { describe, it, expect } from 'vitest';
import { fakeForm, createFakeWebApi } from 'vela-client/testing';
import { AccountMain, account_customertypecode } from '@generated';
import { AccountForm } from './main';

describe('AccountForm (example)', () => {
  // Layer 1: form rules, driven with the fakeForm accessors (no browser, no Dataverse).
  it('hides Details and relaxes industry for a competitor', async () => {
    const form = fakeForm(AccountMain, AccountForm);
    await form.load();
    form.fields.customertypecode.setValue(account_customertypecode.Competitor);
    form.fields.customertypecode.fireOnChange();
    expect(form.tabs.details.getVisible()).toBe(false);
    expect(form.fields.industrycode.getRequiredLevel()).toBe('none');
  });

  it('blocks save when the account name is empty', async () => {
    const form = fakeForm(AccountMain, AccountForm);
    await form.load();
    expect((await form.triggerSave()).prevented).toBe(true);
  });

  // Layer 2: a form method that calls createWebApi(), tested with a fake ambient Xrm.WebApi.
  it('reads related contacts through the typed Web API', async () => {
    const accountId = '11111111-1111-1111-1111-111111111111';
    const api = createFakeWebApi();
    await api.createRecord('contact', { fullname: 'Ada Byron', _parentcustomerid_value: accountId });

    // recordId makes this.id non-empty; webApi installs the fake ambient for the form's lifetime.
    const form = fakeForm(AccountMain, AccountForm, { recordId: accountId, webApi: api });
    const rows = await form.instance.relatedContacts();

    expect(rows.map((r) => r.name)).toContain('Ada Byron');
    form.restore();
  });
});
""";

    // Example generated form model (src/generated/forms/account/main.form.ts). The "EXAMPLE" header marks it as a
    // ready-made sample; `vela models --forms` overwrites src/generated with real models from your environment.
    public static string WebResourcesExampleGeneratedForm() =>
"""
// EXAMPLE - regenerate with: vela models --forms --entities account --out src/generated
// Generated by "vela models --forms --lang ts" - do not edit by hand.
import type { FormModel } from 'vela-client';
import type { FormCommandContext } from 'vela-client/ribbon';
import { account_customertypecode, account_industrycode } from '../../option-sets/account';

export const AccountMain = {
  entity: 'account',
  formId: '00000000-0000-0000-0000-000000000001',
  formType: 'Main',
  fields: {
    name: {
      kind: 'string',
      label: 'Account Name',
      required: 'required',
      at: { tab: 'general', section: 'info' },
    },
    customertypecode: {
      kind: 'choice',
      label: 'Relationship Type',
      required: 'none',
      options: account_customertypecode,
      at: { tab: 'general', section: 'info' },
    },
    industrycode: {
      kind: 'choice',
      label: 'Industry',
      required: 'none',
      options: account_industrycode,
      at: { tab: 'details', section: 'pro' },
    },
  },
  layout: {
    tabs: {
      general: {
        label: 'General',
        sections: { info: { label: 'Info', fields: ['name', 'customertypecode'] } },
      },
      details: {
        label: 'Details',
        sections: { pro: { label: 'Professional', fields: ['industrycode'] } },
      },
    },
  },
} as const satisfies FormModel;

export type AccountCmd = FormCommandContext<typeof AccountMain>;
""";

    // Example generated option sets (src/generated/option-sets/account.ts).
    public static string WebResourcesExampleGeneratedOptionSets() =>
"""
// EXAMPLE - regenerate with: vela models --forms --tables --entities account,contact --out src/generated
// Generated by "vela models --forms --lang ts" - do not edit by hand.

export enum account_customertypecode {
  Competitor = 1,
  Customer = 3,
}

export enum account_industrycode {
  Accounting = 1,
  Consulting = 2,
}
""";

    // Example generated table record (src/generated/tables/contact.ts), used by the example form's typed query.
    public static string WebResourcesExampleGeneratedTable() =>
"""
// EXAMPLE - regenerate with: vela models --tables --entities contact --out src/generated
// Generated by "vela models --tables --lang ts" - do not edit by hand.
import type { DataverseRecordMeta } from 'vela-client/webapi';
import { defineTable } from 'vela-client/webapi';

export interface Contact extends DataverseRecordMeta {
  contactid: string;
  fullname?: string | null;
  emailaddress1?: string | null;
  _parentcustomerid_value?: string | null;
  '_parentcustomerid_value@Microsoft.Dynamics.CRM.lookuplogicalname'?: 'account' | 'contact';
}

export const Contact = defineTable<Contact>({ entity: 'contact', primaryId: 'contactid' });
""";

    // Example generated barrel (src/generated/index.ts): named re-exports of the example model set, imported via the
    // @generated alias. `vela models` regenerates this to cover the real models.
    public static string WebResourcesExampleGeneratedBarrel() =>
"""
// EXAMPLE - regenerate with: vela models --forms --tables --entities account,contact --out src/generated
// Generated by "vela models --lang ts" - do not edit by hand.
export { AccountMain } from './forms/account/main.form';
export type { AccountCmd } from './forms/account/main.form';
export { Contact } from './tables/contact';
export { account_customertypecode, account_industrycode } from './option-sets/account';
""";

    // Structural guard: every entry module must export onLoad and onSave. A form web resource whose source forgets
    // the `export const { onLoad, onSave } = defineForm(...)` wiring builds a bundle that exposes nothing, silently.
    // This test walks the entry map in web-resources.entries.ts and fails the build (like the fixture drift-guard)
    // the moment one stops exporting its FormXML handlers.
    public static string WebResourcesEntriesTest() =>
$$"""
import { describe, it, expect } from 'vitest';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { entries } from '../web-resources.entries';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const sources = Object.values(entries) as string[];

describe('web resource entries expose their wiring', () => {
  it('has at least one entry', () => expect(sources.length).toBeGreaterThan(0));
  for (const src of sources) {
    if (src.includes('/Ribbon/')) {
      // A ribbon command module must export at least one command (the bootstrapCommands map).
      it(`${src} exports commands`, async () => {
        const mod = await import(join(root, src));
        const commands = mod.commands as Record<string, unknown> | undefined;
        expect(commands && Object.keys(commands).length > 0).toBe(true);
      });
    } else {
      // A form-script module must export onLoad/onSave (defineForm wiring present).
      it(`${src} exports onLoad/onSave`, async () => {
        const mod = await import(join(root, src));
        expect(typeof mod.onLoad).toBe('function');
        expect(typeof mod.onSave).toBe('function');
      });
    }
  }
});
""";

    // ---- Root files -------------------------------------------------------

    public static string Gitignore() =>
$$"""
## .NET / Visual Studio
bin/
obj/
*.user
*.suo
.vs/
artifacts/

## Signing - never commit a code-signing certificate
*.pfx
""";

    public static string DirectoryBuildProps() =>
$$"""
<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>disable</ImplicitUsings>
    <!-- Early-bound models and plugin code follow the classic non-nullable Dataverse conventions. -->
    <Nullable>disable</Nullable>
    <!-- These projects pin package versions inline. Disable central package management explicitly so the solution
         builds even if it is created inside a repository that has its own Directory.Packages.props. -->
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
</Project>
""";

    // A solution-root Directory.Packages.props. MSBuild imports the NEAREST Directory.Packages.props walking up the
    // folder tree, and the mere presence of one with ManagePackageVersionsCentrally=true switches on Central Package
    // Management. Emitting this file makes the scaffold's own the nearest one, shadowing any parent inherited from an
    // enclosing repository and keeping CPM off - so the inline PackageReference versions stay valid (no NU1008). To
    // adopt CPM, set this to true and move each version to a <PackageVersion> item here. (#315)
    public static string DirectoryPackagesProps() =>
"""
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
</Project>
""";

    public static string Solution(IReadOnlyList<ProjectEntry> projects)
    {
        StringBuilder sb = new();
        sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
        sb.AppendLine("# Visual Studio Version 17");
        sb.AppendLine("VisualStudioVersion = 17.0.31903.59");
        sb.AppendLine("MinimumVisualStudioVersion = 10.0.40219.1");

        foreach (ProjectEntry project in projects)
        {
            sb.AppendLine($"Project(\"{{{project.ProjectTypeGuid}}}\") = \"{project.Name}\", \"{project.RelativePath}\", \"{{{project.ProjectGuid.ToString().ToUpperInvariant()}}}\"");
            sb.AppendLine("EndProject");
        }

        sb.AppendLine("Global");
        sb.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
        sb.AppendLine("\t\tDebug|Any CPU = Debug|Any CPU");
        sb.AppendLine("\t\tRelease|Any CPU = Release|Any CPU");
        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
        foreach (ProjectEntry project in projects)
        {
            string g = "{" + project.ProjectGuid.ToString().ToUpperInvariant() + "}";
            sb.AppendLine($"\t\t{g}.Debug|Any CPU.ActiveCfg = Debug|Any CPU");
            sb.AppendLine($"\t\t{g}.Debug|Any CPU.Build.0 = Debug|Any CPU");
            sb.AppendLine($"\t\t{g}.Release|Any CPU.ActiveCfg = Release|Any CPU");
            sb.AppendLine($"\t\t{g}.Release|Any CPU.Build.0 = Release|Any CPU");
        }

        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("\tGlobalSection(SolutionProperties) = preSolution");
        sb.AppendLine("\t\tHideSolutionNode = FALSE");
        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("EndGlobal");
        return sb.ToString();
    }

    // Deterministic GUID from a name (MD5-derived) so re-running init produces a stable solution file.
    public static Guid StableGuid(string name)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(name));
        return new Guid(hash);
    }

    public static string RootReadme(ScaffoldOptions options)
    {
        string prefix = options.Prefix;
        string integrationsLine = options.IncludeIntegrations
            ? $"- `src/{prefix}.Integrations` - a second plugin package (same mechanics as Plugins; \"Integrations\" is a naming convention for integration-facing steps/APIs).\n"
            : string.Empty;
        string consoleLine = options.IncludeConsole
            ? $"- `src/{prefix}.ConsoleApp` - a .NET 10 console app that connects to Dataverse with `ServiceClient` for ad-hoc/programmatic access.\n"
            : string.Empty;
        string integrationsTestsLine = options.IncludeTests && options.IncludeIntegrations
            ? $"- `tests/{prefix}.Integrations.Tests` - unit tests for the Integrations sample Custom APIs using `Vela.Testing`.\n"
            : string.Empty;
        string testsLine = options.IncludeTests
            ? $"- `tests/{prefix}.Plugins.Tests` - unit tests for the sample plugins/Custom APIs using `Vela.Testing` (no Dataverse connection).\n{integrationsTestsLine}"
            : string.Empty;
        string testsRestoreNote = options.IncludeTests
            ? " The test project additionally uses `Vela.Testing`, which is distributed the same way."
            : string.Empty;
        string testsSection = options.IncludeTests ? TestsSection(prefix, options.IncludeIntegrations) : string.Empty;
        string integrationsLayout = options.IncludeIntegrations
            ? $"\nThe `{prefix}.Integrations` project follows the same by-table layout and conventions; it ships its own\nsamples (an account sync router with a bound account-reconcile Custom API, a contact sync plugin with a\nbound contact-export Custom API, and a global heartbeat Custom API) so you can compare a second plugin\npackage side by side.\n"
            : string.Empty;
        // Signing is an Integrations-only concern, so the whole section is omitted when that project is excluded.
        string signingSection = !options.IncludeIntegrations
            ? string.Empty
            : options.ManagedIdentity ? SigningSectionEnabled(prefix) : SigningSectionDisabled(prefix);

        return
$$"""
# {{options.SolutionName}}

Dataverse plugin solution generated by `vela init`. It builds on
`Vela.Plugins` (v{{options.CoreVersion}}) and is ready to register with `vela`.

## Projects

- `src/{{prefix}}.Shared` - early-bound entity models (`Account`, `Contact`) and shared services, referenced by the plugin projects.
- `src/{{prefix}}.Plugins` - your plugin package. Builds a Dataverse plugin-package `.nupkg` (not a bare `.dll`).
{{integrationsLine}}{{testsLine}}{{consoleLine}}
> **Restore source:** the plugin projects reference `Vela.Plugins` from **nuget.org** (the default package
> source), so `dotnet restore` / `dotnet build` work out of the box.{{testsRestoreNote}} Add your own
> `nuget.config` only if you resolve packages from a private feed.

## Build the plugin package(s)

```powershell
dotnet build {{options.SolutionName}}.sln -c Release
```

The Release **build** produces each plugin-package `.nupkg` under `artifacts/` (via `GeneratePackageOnBuild` +
`PackageOutputPath`), containing the plugin assembly plus its dependent assemblies (`{{prefix}}.Shared.dll`,
`Vela.Plugins.dll`). Use `dotnet build`, not `dotnet pack`: the Power Platform plugin SDK bundles the assemblies
during the build (from the publish output), so a cold `dotnet pack` emits an assembly-less package that
`vela plan` rejects with "No assemblies found".

{{testsSection}}{{signingSection}}

## Register with Dataverse

```powershell
# Preview (read-only)
vela plan --package artifacts/{{prefix}}.Plugins.1.0.0.nupkg --interactive --verbose

# Apply (idempotent create/update/delete). apply writes by default and prompts on a terminal;
# add --auto-approve to skip the prompt in CI.
vela apply --package artifacts/{{prefix}}.Plugins.1.0.0.nupkg --interactive --verbose
```

## What's inside the plugin project

Code is organised **by table** - each table gets a folder with `Plugins/`, `Services/` and/or
`CustomApis/` subfolders; unbound (global) Custom APIs live under `GlobalCustomApis/`. Each file's
namespace matches its folder (the .NET default), so e.g. `Account/Plugins/` is `{{prefix}}.Plugins.Account.Plugins`:

```text
{{prefix}}.Plugins/
  Account/
    Plugins/AccountPostCreatePlugin.cs          # typed post-stage handler: schedules a 7-day follow-up task + DI
    Services/AccountFollowUpService.cs          # table-scoped service (only account plugins use it)
    CustomApis/AccountEstimateValueCustomApi.cs # Custom API bound to the account table (typed request/response)
  Contact/
    Plugins/ContactRouterPlugin.cs              # multi-step "router" plugin (a handler method per step)
    Services/ContactDuplicateService.cs         # table-scoped service: duplicate-by-email lookup (a read)
    CustomApis/ContactValidateCustomApi.cs      # Custom API bound to the contact table (typed request/response)
  GlobalCustomApis/PingCustomApi.cs             # global (unbound) Custom API, lightweight style
```

Add a new table by creating `<Table>/Plugins/`, `<Table>/Services/` and/or `<Table>/CustomApis/` folders -
or let the CLI place a new file for you (it infers the namespace from the project):

```powershell
vela add plugin AccountValidatorPlugin --entity Account --message Update
vela add customapi RecalculateTotals          # global (unbound) Custom API
vela add customapi ExportContact --entity Contact   # Custom API bound to a table
```
{{integrationsLayout}}
## Registering plugin steps

A plugin derives from `EntityPluginBase<TEntity>` and declares its steps in the `RegisterPluginSteps()`
override with the fluent `RegisterStep()` builder - no step XML and no manual `pac` registration. The chain is
guided: pick the message, then the stage, then the mode, then the `With*` modifiers, and always end with
`.Handle(...)`:

```csharp
protected override void RegisterPluginSteps()
{
    RegisterStep().OnCreate()                        // message: OnCreate/OnUpdate/OnDelete/OnMessage("...")
        .PostOperation()                             // stage: .PreValidation() | .PreOperation() | .PostOperation()
        .Sync()                                      // .Sync() | .Async() (Async is PostOperation-only)
        .FilterOn(a => a.Name)                  // (create/update) attributes that trigger the step
        .WithDescription("Runs after an account is created.")
        .WithPostImage(a => a.Name)                  // snapshot after the operation, limited to these columns
        .Handle(OnAccountCreated);                   // void OnAccountCreated(ICreatePostContext<Account> ctx)
}
```

`vela plan` / `vela apply` read these definitions from the built assembly and create/update the matching SDK
message-processing steps and images in Dataverse - the registration lives in code, next to the handler it
configures.

### Single-step vs multi-step

- **Single-step** - register one step bound to one handler. The handler takes the stage-typed context
  (`ctx.Request` / `ctx.Response`, with the record at `ctx.Request.Target`). See `AccountPostCreatePlugin`.
- **Multi-step ("router")** - register several steps in one class, giving each its own `.Handle(Method)`; the
  framework dispatches to the right one. Each handler takes the message-typed context (for example
  `ICreatePostContext<TEntity>` / `IUpdatePostContext<TEntity>`), exposing a strongly-typed `ctx.Request` (and, in a
  Post step, `ctx.Response`). See `ContactRouterPlugin`.

Every step must end with `.Handle(...)`; a chain that stops earlier does not compile (analyzer VELA002).

```csharp
RegisterStep().OnUpdate()
    .PostOperation().Sync()
    .FilterOn(c => c.LastName)                  // (update) fire only when these columns change
    .WithPreImage(c => c.LastName)                   // before-snapshot for the handler
    .Handle(OnLastNameChanged);                      // void OnLastNameChanged(IUpdatePostContext<Contact> ctx)
```

### Builder options

| Builder call | What it does |
| --- | --- |
| `.OnCreate()` … `.OnUpdate()` … `.OnMessage("custom_action")` | The message the step listens to. |
| `.PreValidation()` / `.PreOperation()` / `.PostOperation()` | Pipeline stage. |
| `.Sync()` / `.Async()` | Execution mode (Async is PostOperation-only). |
| `.FilterOn(...)` | (create/update) fire only when one of the listed columns changes. |
| `.WithPreImage(...)` / `.WithPostImage(...)` | Capture a before/after snapshot, scoped to named columns. |
| `.WithExecutionOrder(n)` | Step rank when several steps share a message + stage. |
| `.WithImpersonation(...)` | Run as `CallingUser` or `PluginOwner`. |
| `.WithDescription(...)` | Step description shown in Dataverse. |
| `.Disabled()` | Register the step dormant (kill-switch / phased rollout). |
| `.Handle(M)` | The handler the step dispatches to; takes the message-typed context. Required, last in the chain. |

Read images back in a handler with `ctx.GetPreImage<TEntity>()` / `ctx.GetPostImage<TEntity>()`.

## Services & dependency injection

Plugins and Custom APIs get a small per-execution dependency-injection container. Register your services in
the optional `ConfigureServices(...)` override, then resolve them anywhere in the plugin with
`ctx.GetRequiredService<T>()`. The container is rebuilt for every execution, so a service never leaks state
between plugin runs.

### Two kinds of service (both seeded)

- **Cross-table** - lives in `{{prefix}}.Shared/Services` and can be referenced by any plugin project.
  Example: `GreetingService` (no Dataverse dependency).
- **Table-scoped** - lives next to the table under `{{prefix}}.Plugins/<Table>/Services` because only that
  table's plugins use it. Two examples ship:
  - `Account/Services/AccountFollowUpService.cs` - creates a follow-up `task` seven days after an account is
    created (a Dataverse **write**); used by `AccountPostCreatePlugin`.
  - `Contact/Services/ContactDuplicateService.cs` - looks up a possible duplicate by email (a Dataverse
    **read**); used by `ContactRouterPlugin`, so router plugins get DI too.

Promote a service from a table folder up to `Shared/Services` only once a second table needs it.

### What you can inject out of the box

These are pre-registered for every execution - request them from the resolver (`r`) or from `ctx`:

| Service | Notes |
| --- | --- |
| `IOrganizationService` | Runs as the **initiating user**. For elevated access use `ctx.AsSystem()`. |
| `ITracingService` | Plugin trace log (`ctx.Trace(...)` wraps it). |
| `IPluginExecutionContext` | The raw execution context. |
| `ILocalPluginContext` | The framework context itself. |
| `ILogger` | Logging abstraction. |
| `Func<Guid?, IOrganizationService>` | Factory: pass a user id, or `null` for the system user. |
| `IPluginCacheService` | Cache that survives across executions. |

### Registering services

```csharp
protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context)
{
    // Register the interface, build the implementation. Pull dependencies from the resolver `r`.
    services.AddScoped<IAccountFollowUpService>(r =>
        new AccountFollowUpService(r.GetRequiredService<IOrganizationService>()));

    // A service with no Dataverse dependency:
    services.AddScoped<IGreetingService>(_ => new GreetingService());
}
```

- `AddScoped<T>` - one shared instance per execution (the usual choice).
- `AddTransient<T>` - a fresh instance every time it is resolved.

### Resolving and using a service

```csharp
private void OnAccountCreated(ICreatePostContext<Account> ctx)
{
    Account target = ctx.GetTarget<Account>();
    IAccountFollowUpService followUp = ctx.GetRequiredService<IAccountFollowUpService>();
    Guid taskId = followUp.CreateFollowUpTask(target.Id, target.Name);
    ctx.Trace("Created follow-up task {0}.", taskId);
}
```

`ctx.GetRequiredService<T>()` throws a clear error if the type was not registered; `ctx.GetService<T>()`
returns `null` instead.

### Best practices

- **Depend on an interface, register the implementation** - keeps handlers thin and unit-testable.
- **Keep services stateless.** The container is per-execution; never stash an `IOrganizationService` (or
  anything request-specific) in a `static`/singleton field.
- **Inject `IOrganizationService` through the constructor** (as the samples do) instead of reaching into the
  context from deep in your code - it makes the dependency explicit and mockable.
- **Mind the user context.** The injected `IOrganizationService` runs as the initiating user; for elevated
  work use `ctx.AsSystem()` or the `Func<Guid?, IOrganizationService>` factory.
- **Place logic by reach** - cross-table logic in `Shared/Services`, table-specific logic in
  `<Table>/Services`.
- **Do light work in constructors.** Services are built lazily on first resolve; keep queries and writes in
  methods, not the constructor.

## Early-bound models

`{{prefix}}.Shared/Models/Generated` ships hand-written `Account` and `Contact` stubs (namespace
`{{prefix}}.Shared.Models.Generated`) so the solution builds with no Dataverse connection. Running `vela models`
into the same folder/namespace **replaces them in place** with the full set - one copy, no duplicates. It reuses
the same connection options as `vela plan`/`apply`:

```powershell
vela models `
  --solution {{prefix}} `
  --out ./src/{{prefix}}.Shared/Models/Generated `
  --namespace {{prefix}}.Shared.Models.Generated
```

`vela models` emits `partial` classes with option-set enums, multi-select choices, alternate-key factories and
DataAnnotations. For the few things it leaves out (relationship navigation properties / `OrganizationServiceContext`
LINQ, localized multi-language labels, custom casing), use `pac modelbuilder` instead.

Add `--roles "<names>"` (or `--solution-roles` with `--solution`) to also generate a dependency-light `Roles`
class into the same `Shared.Models.Generated` folder: `Roles.XName` / `Roles.XId` are usable from plugin code at
runtime, and the harness seeds a role's real privileges in tests via `Role(Roles.XName, Roles.XId, Roles.X)`.
""";
    }

    // README signing section when `vela init --managed-identity` wired the build-time signing target into Integrations.
    private static string SigningSectionEnabled(string prefix) =>
$$"""
## Signing the plugin package

This solution wires **opt-in** NuGet signing into the build of the **`{{prefix}}.Integrations`** project (the
integration-facing package - the one most likely to call out using a managed identity). The signature is
applied to the produced `.nupkg`, which is the artifact deployed to Dataverse.

A `SignPluginPackage` MSBuild target runs after the Integrations package is packed and shells out to
`vela sign` (which calls `dotnet nuget sign`). It is **inert until you configure a certificate source**, and
**skipped inside CI** (GitHub Actions / Azure DevOps), which should own their own signing step - so there is
never double-signing.

> The target invokes the `vela` CLI, so it must be installed as a global .NET tool and on your `PATH`:
> `dotnet tool install --global Vela.Cli`.

### Configure one certificate source

The target picks its method from MSBuild properties - set them on the `dotnet build` command (or in
`Directory.Build.props`). The fingerprint takes precedence if more than one is set.

| Property | Method |
| --- | --- |
| `PluginSigningFingerprint` | SHA-256 fingerprint of a cert in `CurrentUser\My` - **recommended**, no secret on disk |
| `PluginSigningCertificate` | path to a `.pfx` file (combine with one of the next two) |
| `PluginSigningCredential` | a Windows Credential Manager entry holding the `.pfx` password - **preferred** over a plaintext password |
| `PluginSigningPassword` | the `.pfx` password directly - avoid persisting it |

### Method A - certificate store (recommended)

1. Import your code-signing certificate into `CurrentUser\My` once (no secret left on disk).
2. Get its **SHA-256 fingerprint** - `dotnet nuget sign` needs SHA-256, not the SHA-1 thumbprint shown in `certmgr`:

```powershell
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -match 'YourCertName'
[BitConverter]::ToString($cert.GetCertHash('SHA256')).Replace('-','')
```

3. Build with that fingerprint (the signing target runs after the package is generated on build):

```powershell
dotnet build src/{{prefix}}.Integrations -c Release -p:PluginSigningFingerprint=<fingerprint>
```

### Method B - `.pfx` file

Keep a `.pfx` file (gitignored) and store its password in Windows Credential Manager rather than in plaintext:

```powershell
# store the password once (DPAPI-encrypted, per-user); 'AcmeSigningCert' is the entry name you choose
cmdkey /generic:AcmeSigningCert /user:plugin /pass:<pfxPassword>

dotnet build src/{{prefix}}.Integrations -c Release `
  -p:PluginSigningCertificate=C:\certs\cert.pfx -p:PluginSigningCredential=AcmeSigningCert
```

### Manual / pipeline signing

`vela sign` signs with an RFC-3161 timestamp (so the signature survives certificate expiry). Sign a packed
`.nupkg` directly:

```powershell
vela sign --package artifacts/{{prefix}}.Integrations.1.0.0.nupkg --fingerprint <fingerprint>
vela sign --package artifacts/{{prefix}}.Integrations.1.0.0.nupkg --pfx cert.pfx --pfx-credential AcmeSigningCert
```

In a **pipeline**, the build target stays inert (CI is detected); add an explicit sign step and pass the
certificate as a base64 secret rather than a file:

```powershell
vela sign --package artifacts/{{prefix}}.Integrations.1.0.0.nupkg `
  --pfx-base64 $env:CODE_SIGN_PFX_BASE64 --pfx-password $env:CODE_SIGN_PFX_PASSWORD
```

> `vela sign` uses `dotnet nuget sign`, so it runs anywhere the .NET SDK does (Windows, Linux, macOS).

""";

    // README signing section when signing was not requested (but Integrations exists, so it is relevant).
    private static string SigningSectionDisabled(string prefix) =>
$$"""
## Signing the plugin package

The build does **not** sign the assembly. If the `{{prefix}}.Integrations` plugin uses a **managed identity**
(and therefore needs publisher identity + tamper-evidence on the package), re-scaffold with
`vela init <name> --managed-identity` to add an opt-in, CI-gated signing step to the Integrations build, or
sign a packed `.nupkg` yourself:

```powershell
vela sign --package artifacts/{{prefix}}.Integrations.1.0.0.nupkg --fingerprint <fingerprint>
```

""";

    // README section describing the generated test project (only emitted when tests are included).
    private static string TestsSection(string prefix, bool includeIntegrations)
    {
        string integrationsTests = includeIntegrations
            ? $"\nThe `{prefix}.Integrations.Tests` project does the same for the Integrations package " +
              $"(`AccountReconcile`, `ContactExport` and the global heartbeat Custom APIs):\n\n" +
              $"```powershell\ndotnet test tests/{prefix}.Integrations.Tests -c Release\n```\n"
            : string.Empty;

        return
$$"""
## Test the plugins

The `{{prefix}}.Plugins.Tests` project unit-tests the sample plugins and Custom APIs with
[`Vela.Testing`](https://www.nuget.org/packages/Vela.Testing) - a fake execution context plus an in-memory
Dataverse, so the tests run **without a connection and without a mocking library**:

```powershell
dotnet test tests/{{prefix}}.Plugins.Tests -c Release
```

What the samples show:

- `Account/Plugins/AccountPostCreatePluginTests.cs` - runs `AccountPostCreatePlugin` against an
  `InMemoryOrganizationService` and asserts the follow-up `task` it creates (subject + regarding object).
- `Account/Plugins/AccountRelatedContactsPluginTests.cs` - runs `AccountRelatedContactsPlugin`, which counts an
  account's related contacts with the **typed FetchXML builder** (`Query.For<Contact>().ToFetchXml()`, checked at
  compile time), and asserts the traced count.
- `Contact/Plugins/ContactRouterPluginTests.cs` - drives a specific step of the multi-step router and asserts
  on the emitted traces via `TraceCapture`, seeding a duplicate contact first.
- `Account/CustomApis/AccountEstimateValueCustomApiTests.cs` - invokes the typed, bound Custom API and checks
  the mapped output parameter.
- `Contact/CustomApis/ContactValidateCustomApiTests.cs` - invokes the typed, contact-bound Custom API.
- `GlobalCustomApis/PingCustomApiTests.cs` - invokes the lightweight global Custom API.
- `Security/SecurityExampleTests.cs` - shows the harness's opt-in **security model**: `WithSecurity(...)` declares
  business units, roles and users, `WithCurrentUser(...)` picks who is acting, and the service then enforces table
  privileges (a denied Retrieve throws) and trims `RetrieveMultiple` to the caller's readable records. Off by
  default, so the other tests keep full access.
{{integrationsTests}}
A plugin with a `(unsecureConfig, secureConfig)` constructor is run with `.Execute(new MyPlugin("", ""))`;
the trigger is auto-derived from a single-step plugin's own registration, while a multi-step router needs the
step stated explicitly (`OnCreate().Stage(...).Mode(...)`) to select the handler under test.


""";
    }

    // ---- Shared project ---------------------------------------------------

    public static string SharedCsproj(ScaffoldOptions options) =>
$$"""
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <!-- net462 is consumed by the plugin projects; net10.0 by the ConsoleApp. -->
    <TargetFrameworks>net462;net10.0</TargetFrameworks>
    <RootNamespace>{{options.Prefix}}.Shared</RootNamespace>
    <AssemblyName>{{options.Prefix}}.Shared</AssemblyName>
  </PropertyGroup>

  <!-- net462: the plugin sandbox provides the SDK, so these reference assemblies are PrivateAssets. -->
  <ItemGroup Condition="'$(TargetFramework)' == 'net462'">
    <PackageReference Include="Microsoft.CrmSdk.CoreAssemblies" Version="{{CrmCoreAssembliesVersion}}" PrivateAssets="all" />
    <!-- .NET Framework 4.6.2 has no in-box System.ValueTuple; the generated Roles class exposes grant tuples.
         Compile-time reference only (the type forwards to mscorlib on the 4.7.2+ Dataverse sandbox at runtime). -->
    <PackageReference Include="System.ValueTuple" Version="4.5.0" />
    <!-- `vela models` emits [StringLength]/[Range] DataAnnotations by default; on net462 those types live in the
         System.ComponentModel.DataAnnotations framework assembly, which is not referenced by default. -->
    <Reference Include="System.ComponentModel.DataAnnotations" />
    <!-- Pin the patched System.Security.Cryptography.Xml (a transitive SDK dependency ships an advisory version,
         NU1903). PrivateAssets so it stays compile-only and is not deployed to the plugin sandbox, which
         provides its own. -->
    <PackageReference Include="System.Security.Cryptography.Xml" Version="{{CryptographyXmlVersion}}" PrivateAssets="all" />
  </ItemGroup>

  <!-- net10.0: the cross-platform client supplies Microsoft.Xrm.Sdk for the ConsoleApp. -->
  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
    <PackageReference Include="Microsoft.PowerPlatform.Dataverse.Client" Version="{{DataverseClientVersion}}" />
    <!-- Pin the patched System.Security.Cryptography.Xml over the client's advisory transitive version (NU1903). -->
    <PackageReference Include="System.Security.Cryptography.Xml" Version="{{CryptographyXmlVersion}}" />
  </ItemGroup>

  <!-- Vela.Messages holds the SDK-only TypedRequest/TypedResponse base types and multi-targets net462;net10.0, so
       generated Custom API request/response contracts compile here and can be called from anywhere (plugins on
       net462, the ConsoleApp / integrations on net10.0). -->
  <ItemGroup>
    <PackageReference Include="Vela.Messages" Version="{{options.CoreVersion}}" />
  </ItemGroup>

</Project>
""";

    public static string AccountModel(string prefix) =>
$$"""
namespace {{prefix}}.Shared.Models.Generated
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Microsoft.Xrm.Sdk.Client;

    /// <summary>Minimal early-bound model for the <c>account</c> table.</summary>
    [EntityLogicalName(EntityLogicalNameValue)]
    public sealed class Account : Entity
    {
        public const string EntityLogicalNameValue = "account";

        public Account()
            : base(EntityLogicalNameValue)
        {
        }

        [AttributeLogicalName("accountid")]
        public Guid? AccountId
        {
            get => GetAttributeValue<Guid?>("accountid");
            set => SetAttributeValue("accountid", value);
        }

        [AttributeLogicalName("name")]
        public string Name
        {
            get => GetAttributeValue<string>("name");
            set => SetAttributeValue("name", value);
        }

        [AttributeLogicalName("telephone1")]
        public string Telephone1
        {
            get => GetAttributeValue<string>("telephone1");
            set => SetAttributeValue("telephone1", value);
        }

        [AttributeLogicalName("revenue")]
        public Money Revenue
        {
            get => GetAttributeValue<Money>("revenue");
            set => SetAttributeValue("revenue", value);
        }
    }
}
""";

    public static string ContactModel(string prefix) =>
$$"""
namespace {{prefix}}.Shared.Models.Generated
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Microsoft.Xrm.Sdk.Client;

    /// <summary>Minimal early-bound model for the <c>contact</c> table.</summary>
    [EntityLogicalName(EntityLogicalNameValue)]
    public sealed class Contact : Entity
    {
        public const string EntityLogicalNameValue = "contact";

        public Contact()
            : base(EntityLogicalNameValue)
        {
        }

        [AttributeLogicalName("contactid")]
        public Guid? ContactId
        {
            get => GetAttributeValue<Guid?>("contactid");
            set => SetAttributeValue("contactid", value);
        }

        [AttributeLogicalName("firstname")]
        public string FirstName
        {
            get => GetAttributeValue<string>("firstname");
            set => SetAttributeValue("firstname", value);
        }

        [AttributeLogicalName("lastname")]
        public string LastName
        {
            get => GetAttributeValue<string>("lastname");
            set => SetAttributeValue("lastname", value);
        }

        [AttributeLogicalName("emailaddress1")]
        public string EMailAddress1
        {
            get => GetAttributeValue<string>("emailaddress1");
            set => SetAttributeValue("emailaddress1", value);
        }

        [AttributeLogicalName("parentcustomerid")]
        public EntityReference ParentCustomerId
        {
            get => GetAttributeValue<EntityReference>("parentcustomerid");
            set => SetAttributeValue("parentcustomerid", value);
        }
    }
}
""";

    public static string GreetingServiceInterface(string prefix) =>
$$"""
namespace {{prefix}}.Shared.Services
{
    /// <summary>Example shared domain service resolved through the plugin dependency container.</summary>
    public interface IGreetingService
    {
        string BuildGreeting(string name);
    }
}
""";

    public static string GreetingService(string prefix) =>
$$"""
namespace {{prefix}}.Shared.Services
{
    using System;

    /// <inheritdoc />
    public sealed class GreetingService : IGreetingService
    {
        public string BuildGreeting(string name)
        {
            return string.IsNullOrWhiteSpace(name)
                ? "Hello from {{prefix}}!"
                : "Hello, " + name + ", from {{prefix}}!";
        }
    }
}
""";

    // ---- Table-scoped services (Plugins project) -------------------------

    public static string AccountFollowUpService(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.Account.Services
{
    using System;
    using Microsoft.Xrm.Sdk;

    /// <summary>
    /// Account-scoped domain service: schedules follow-up activities for an account. Unlike the
    /// cross-table <c>IGreetingService</c> in the Shared project, this service lives with the Account
    /// table because only account plugins use it.
    /// </summary>
    public interface IAccountFollowUpService
    {
        Guid CreateFollowUpTask(Guid accountId, string accountName);
    }

    /// <inheritdoc />
    public sealed class AccountFollowUpService(IOrganizationService service) : IAccountFollowUpService
    {
        // Business rule: chase a newly created account one week later.
        private const int FollowUpDays = 7;

        private readonly IOrganizationService _service = service;

        public Guid CreateFollowUpTask(Guid accountId, string accountName)
        {
            Entity task = new("task")
            {
                ["subject"] = "Follow up with " + accountName,
                ["description"] = "Auto-created " + FollowUpDays + " days after the account was created.",
                ["scheduledend"] = DateTime.UtcNow.AddDays(FollowUpDays),
                ["regardingobjectid"] = new EntityReference("account", accountId),
            };

            return _service.Create(task);
        }
    }
}
""";

    public static string ContactDuplicateService(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.Contact.Services
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Microsoft.Xrm.Sdk.Query;
    using Vela.Querying;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>
    /// Contact-scoped domain service: finds a possible duplicate contact by email. A second
    /// table-scoped service (the first being <c>IAccountFollowUpService</c>) that, in contrast, <b>reads</b>
    /// from Dataverse rather than writing.
    /// </summary>
    public interface IContactDuplicateService
    {
        Guid? FindDuplicateByEmail(string email, Guid excludeContactId);
    }

    /// <inheritdoc />
    public sealed class ContactDuplicateService(IOrganizationService service) : IContactDuplicateService
    {
        private readonly IOrganizationService _service = service;

        public Guid? FindDuplicateByEmail(string email, Guid excludeContactId)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return null;
            }

            // Typed query over the early-bound Contact: table and column names are checked at compile time
            // (a typo won't build). No columns are selected - only the matching row's Id is needed.
            EntityCollection matches = _service.RetrieveMultiple(
                Query.For<Contact>()
                    .Where(c => c.EMailAddress1, ConditionOperator.Equal, email)
                    .Where(c => c.ContactId, ConditionOperator.NotEqual, excludeContactId)
                    .Top(1)
                    .Build());

            return matches.Entities.Count > 0 ? matches.Entities[0].Id : (Guid?)null;
        }
    }
}
""";

    // ---- Plugin / Integration project file -------------------------------

    public static string PluginCsproj(string projectName, string sharedProjectName, string coreVersion, bool withSigningTarget = false)
    {
        string signingTarget = withSigningTarget ? SigningBuildTarget() : string.Empty;
        return
$$"""
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net462</TargetFramework>
    <RootNamespace>{{projectName}}</RootNamespace>
    <AssemblyName>{{projectName}}</AssemblyName>
    <PackageId>{{projectName}}</PackageId>
    <Version>1.0.0</Version>
    <!-- Auto-increment the assembly version on every build. Dataverse caches plugin assemblies by version, so
         re-deploying changed code under the SAME version can serve a stale assembly and fail at runtime with
         "The plug-in type could not be found in the plug-in assembly". The 1.0.* wildcard needs a
         non-deterministic build. (vela apply also fails fast if you add a new plugin type without bumping this.) -->
    <Deterministic>false</Deterministic>
    <AssemblyVersion>1.0.*</AssemblyVersion>
    <!-- The 1.0.* wildcard is deliberate (see above), so silence CS7035 ("version string not in the recommended
         major.minor.build.revision format") - the compiler's nag about the wildcard form, not a real problem. -->
    <NoWarn>$(NoWarn);CS7035</NoWarn>
    <!-- The Power Platform plugin SDK bundles the assembly plus its dependent assemblies into a plugin-package
         .nupkg. That bundling runs on BUILD (it packs the publish output), so `dotnet build -c Release` produces
         the deployable package, NOT `dotnet pack` (run cold it skips the publish and emits an assembly-less
         package that vela plan rejects with "No assemblies found"). PackageOutputPath drops the package into the
         solution's artifacts folder so vela plan can read it from there. -->
    <GeneratePackageOnBuild>true</GeneratePackageOnBuild>
    <PackageOutputPath>$(MSBuildProjectDirectory)\..\..\artifacts</PackageOutputPath>
    <IsWorkflowActivity>false</IsWorkflowActivity>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.CrmSdk.CoreAssemblies" Version="{{CrmCoreAssembliesVersion}}" PrivateAssets="all" />
    <PackageReference Include="Microsoft.PowerApps.MSBuild.Plugin" Version="{{PluginSdkVersion}}" PrivateAssets="all" />
    <PackageReference Include="Vela.Plugins" Version="{{coreVersion}}" />
    <!-- HttpClient on net462 binds against this GAC facade; without it, outbound HTTP (e.g. a managed-identity
         call) fails to compile/load. Harmless when unused. Enable TLS 1.2 once before the call (see the docs). -->
    <Reference Include="System.Net.Http" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\{{sharedProjectName}}\{{sharedProjectName}}.csproj" />
  </ItemGroup>

  <!-- Register this assembly's early-bound entity types with the SDK serializer. Without it, passing an
       early-bound entity (e.g. new Account { ... }) to ctx.UserService.Create/Update inside a plugin fails at
       runtime with a SerializationException ("The deserializer has no knowledge of any type that maps to the
       name '<namespace>:Account'"), because the sandbox cannot resolve the generated subclass. -->
  <ItemGroup>
    <AssemblyAttribute Include="Microsoft.Xrm.Sdk.Client.ProxyTypesAssemblyAttribute" />
  </ItemGroup>

  <!-- The package version is pinned to 1.0.0 (so `vela plan/apply` can reference a stable artifacts path) while
       the assembly version auto-increments (1.0.*). Because the .nupkg filename never changes, Pack's incremental
       check can keep a STALE package after a code-only change and deploy an old assembly. Delete the package
       before each build so GeneratePackageOnBuild always repacks it from the freshly built assembly. -->
  <Target Name="RefreshPluginPackage" BeforeTargets="Build">
    <PropertyGroup>
      <_PluginPackagePath>$([MSBuild]::EnsureTrailingSlash('$(PackageOutputPath)'))$(PackageId).$(Version).nupkg</_PluginPackagePath>
    </PropertyGroup>
    <Delete Files="$(_PluginPackagePath)" Condition="Exists('$(_PluginPackagePath)')" />
  </Target>
{{signingTarget}}
</Project>
""";
    }

    // Opt-in MSBuild target that NuGet-signs the packed plugin package on a developer machine by shelling
    // out to `vela sign`. Inert until a certificate source is configured, and suppressed inside CI (each CI
    // system sets its own variable) so the pipeline owns its own signing step - no double-signing.
    private static string SigningBuildTarget() =>
$$"""

  <!--
    Opt-in local package signing (a trusted signature is needed when the plugin uses a managed identity).
    Configure ONE certificate source and `dotnet build -c Release` signs automatically (this target runs
    AfterTargets="Pack", which fires during the build via GeneratePackageOnBuild); inert otherwise, and skipped
    inside CI (GITHUB_ACTIONS / TF_BUILD), which should own their own signing step.
    Requires the `vela` CLI on PATH: dotnet tool install -g Vela.Cli

      PluginSigningFingerprint : SHA-256 fingerprint of a cert in CurrentUser\My (recommended; no secret on disk)
      PluginSigningCertificate : path to a .pfx file, plus ONE of -
        PluginSigningCredential : a Windows Credential Manager entry holding the password (preferred)
        PluginSigningPassword   : the password directly (avoid persisting it)
  -->
  <Target Name="SignPluginPackage" AfterTargets="Pack"
          Condition=" ('$(PluginSigningFingerprint)' != '' or '$(PluginSigningCertificate)' != '') and '$(GITHUB_ACTIONS)' != 'true' and '$(TF_BUILD)' != 'True' ">
    <!-- EnsureTrailingSlash keeps the path correct whether or not PackageOutputPath already ends in a separator (e.g. `pack -o artifacts`). -->
    <PropertyGroup>
      <_SignPackage>$([MSBuild]::EnsureTrailingSlash('$(PackageOutputPath)'))$(PackageId).$(PackageVersion).nupkg</_SignPackage>
    </PropertyGroup>
    <!-- 1) Certificate store, by fingerprint (takes precedence). -->
    <Exec Condition=" '$(PluginSigningFingerprint)' != '' "
          Command="vela sign --package &quot;$(_SignPackage)&quot; --fingerprint $(PluginSigningFingerprint)" />
    <!-- 2) .pfx file + password from Windows Credential Manager. -->
    <Exec Condition=" '$(PluginSigningFingerprint)' == '' and '$(PluginSigningCertificate)' != '' and '$(PluginSigningCredential)' != '' "
          Command="vela sign --package &quot;$(_SignPackage)&quot; --pfx &quot;$(PluginSigningCertificate)&quot; --pfx-credential $(PluginSigningCredential)" />
    <!-- 3) .pfx file + password passed directly. -->
    <Exec Condition=" '$(PluginSigningFingerprint)' == '' and '$(PluginSigningCertificate)' != '' and '$(PluginSigningCredential)' == '' "
          Command="vela sign --package &quot;$(_SignPackage)&quot; --pfx &quot;$(PluginSigningCertificate)&quot; --pfx-password $(PluginSigningPassword)" />
  </Target>
""";

    // ---- Plugin code (Plugins project) -----------------------------------

    public static string AccountRelatedContactsPlugin(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.Account.Plugins
{
    using System;
    using Vela.Caching;
    using Vela.Constants;
    using Vela.Context;
    using Vela.Plugin;
    using Vela.Querying;
    using Microsoft.Xrm.Sdk;
    using Microsoft.Xrm.Sdk.Query;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>
    /// Single-step plugin: after an account is updated, counts the contacts that reference it. The query is
    /// authored with the TYPED query builder <c>Query.For&lt;Contact&gt;()</c> - table and column names are checked
    /// at compile time (a typo won't build). The builder emits either FetchXML (<c>ToFetchXml()</c>) or a
    /// <c>QueryExpression</c> (<c>Build()</c>); this sample runs the <c>QueryExpression</c>. The count is served
    /// through the built-in <c>IPluginCacheService</c> (read-through, 5-minute TTL) so a hot account is not
    /// re-queried on every update.
    /// </summary>
    public sealed partial class AccountRelatedContactsPlugin(string unsecureConfig, string secureConfig) : EntityPluginBase<Account>(unsecureConfig, secureConfig)
    {
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnUpdate()
                .PostOperation()
                .Sync()
                .FilterOn(a => a.Name)
                .WithDescription("Counts the contacts related to an account after it is updated.")
                .Handle(OnAccountUpdated);
        }

        private void OnAccountUpdated(IUpdatePostContext<Account> ctx)
        {
            Account target = ctx.GetTarget<Account>();

            // Typed query -> a strongly-typed QueryExpression (use ToFetchXml() instead for FetchXML).
            QueryExpression query = Query.For<Contact>()
                .Select(c => c.FirstName, c => c.LastName)
                .Where(c => c.ParentCustomerId, ConditionOperator.Equal, target.Id)
                .Build();

            // Read-through cache with a short TTL: the RetrieveMultiple only runs on a cache miss (or after the
            // entry expires). Key by account so entries never collide. IPluginCacheService is a built-in,
            // process-level service - no registration needed.
            IPluginCacheService cache = ctx.GetRequiredService<IPluginCacheService>();
            int count = cache.GetOrCreate(
                $"account:related-contacts:{target.Id}",
                TimeSpan.FromMinutes(5),
                () => ctx.UserService.RetrieveMultiple(query).Entities.Count);

            ctx.Trace("Account {0} has {1} related contact(s).", target.Id, count);
        }
    }
}
""";

    public static string AccountRelatedContactsPluginTests(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.Tests.Account.Plugins
{
    using System;
    using Vela.Constants;
    using Vela.Steps;
    using Vela.Testing;
    using Microsoft.Xrm.Sdk;
    using Xunit;
    using {{prefix}}.Plugins.Account.Plugins;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>
    /// Tests <see cref="AccountRelatedContactsPlugin"/> against an in-memory Dataverse: seeds an account with two
    /// contacts and asserts the plugin's typed FetchXML query counts them (the query runs through the harness's real
    /// FetchXML path, so the emitted string is exercised end to end).
    /// </summary>
    public sealed class AccountRelatedContactsPluginTests
    {
        [Fact]
        public void Counts_the_related_contacts_via_the_typed_fetchxml_builder()
        {
            Guid accountId = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService().Seed(
                new Account { Id = accountId, Name = "Zava" },
                new Contact { Id = Guid.NewGuid(), FirstName = "Jane", ParentCustomerId = new EntityReference(Account.EntityLogicalNameValue, accountId) },
                new Contact { Id = Guid.NewGuid(), FirstName = "John", ParentCustomerId = new EntityReference(Account.EntityLogicalNameValue, accountId) });

            PluginContextBuilder<Account> builder = PluginContext.For<Account>()
                .OnUpdate().Stage(PluginStages.PostOperation).Sync()
                .WithPrimaryEntity(Account.EntityLogicalNameValue, accountId)
                .WithTarget(new Account { Id = accountId, Name = "Zava (renamed)" })
                .WithOrganizationService(crm);

            builder.Execute(new AccountRelatedContactsPlugin(string.Empty, string.Empty));

            Assert.Contains("has 2 related contact", builder.TraceCapture.Output);
        }
    }
}
""";

    public static string AccountPostCreatePlugin(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.Account.Plugins
{
    using System;
    using Vela.Constants;
    using Vela.Context;
    using Vela.DI;
    using Vela.Plugin;
    using Microsoft.Xrm.Sdk;
    using {{prefix}}.Plugins.Account.Services;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>
    /// After an account is created, schedules a follow-up task a week out. Uses a typed post-stage handler
    /// (<see cref="ICreatePostContext{TEntity}"/>) so <c>ctx.Response.id</c> and <c>ctx.Request.Target</c> are strongly
    /// typed, and shows dependency injection via <see cref="ConfigureServices"/> plus a table-scoped service
    /// (<see cref="IAccountFollowUpService"/>) resolved with <c>ctx.GetRequiredService&lt;T&gt;()</c>.
    /// </summary>
    // partial: the typed image projection is generated as a nested type inside this class (VELA014).
    public sealed partial class AccountPostCreatePlugin(string unsecureConfig, string secureConfig) : EntityPluginBase<Account>(unsecureConfig, secureConfig)
    {
        protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context)
        {
            // The default registrations already expose the initiating-user IOrganizationService; hand it
            // to the table-scoped service.
            services.AddScoped<IAccountFollowUpService>(r => new AccountFollowUpService(r.GetRequiredService<IOrganizationService>()));
        }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate()
                .PostOperation()
                .Sync()
                .FilterOn(a => a.Name)
                .WithDescription("Schedules a 7-day follow-up task after an account is created.")
                .WithPostImage(a => a.Name)
                .Handle(OnAccountCreated);
        }

        // Typed handler: because the step is PostOperation, ctx is an ICreatePostContext<Account>, so ctx.Response
        // (the CreateResponse) and ctx.Request.Target (the created Account) are strongly typed. It is still an
        // ILocalPluginContext, so images and services work as usual.
        // The parameter is the generated typed step context (OnAccountCreatedContext, a subclass of
        // ICreatePostContext<Account>): ctx.PostImage is the captured post-image, typed to the captured columns only.
        private void OnAccountCreated(OnAccountCreatedContext ctx)
        {
            var postImage = ctx.PostImage;
            IAccountFollowUpService followUp = ctx.GetRequiredService<IAccountFollowUpService>();

            Guid taskId = followUp.CreateFollowUpTask(ctx.Response.id, postImage.Name);
            ctx.Trace("Created follow-up task {0} for account '{1}'.", taskId, postImage.Name);
        }
    }
}
""";

    public static string AccountMessageReference(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.Account.Plugins
{
    using Vela.Constants;
    using Vela.Context;
    using Vela.Plugin;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>
    /// REFERENCE / CHEAT-SHEET - one handler per typed message Vela models, showing the strongly-typed context
    /// each receives. Every step is registered <c>.Disabled()</c>, so this class is safe to deploy: nothing fires
    /// until you enable a step. Copy the handler for the message you need into a real plugin, then delete this
    /// file. Each handler infers its request/response from the message (no cast); the primary record is read via
    /// <c>ctx.Request.Target</c> (Create / Update, an early-bound entity) / <c>ctx.Request.Targets</c> (the
    /// SDK-typed <c>EntityReference</c> / <c>EntityCollection</c> for the rest).
    /// </summary>
    public sealed partial class AccountMessageReference(string unsecureConfig, string secureConfig) : EntityPluginBase<Account>(unsecureConfig, secureConfig)
    {
        protected override void RegisterPluginSteps()
        {
            // Create / Update carry a full entity Target; both require at least one filtering attribute.
            RegisterStep().OnCreate().PostOperation().Sync().FilterOn(a => a.Name).Disabled().Handle(OnCreate);
            RegisterStep().OnUpdate().PostOperation().Sync().FilterOn(a => a.Name).Disabled().Handle(OnUpdate);

            // Delete carries an EntityReference Target.
            RegisterStep().OnDelete().PreOperation().Sync().Disabled().Handle(OnDelete);

            // Bulk messages carry a Targets collection; they also require a filtering attribute.
            RegisterStep().OnCreateMultiple().PostOperation().Sync().FilterOn(a => a.Name).Disabled().Handle(OnCreateMultiple);
            RegisterStep().OnUpdateMultiple().PostOperation().Sync().FilterOn(a => a.Name).Disabled().Handle(OnUpdateMultiple);

            // Read messages.
            RegisterStep().OnRetrieve().PostOperation().Sync().Disabled().Handle(OnRetrieve);
            RegisterStep().OnRetrieveMultiple().PostOperation().Sync().Disabled().Handle(OnRetrieveMultiple);

            // Relationship messages.
            RegisterStep().OnAssociate().PostOperation().Sync().Disabled().Handle(OnAssociate);
            RegisterStep().OnDisassociate().PostOperation().Sync().Disabled().Handle(OnDisassociate);

            // Merge and sharing (access) messages.
            RegisterStep().OnMerge().PostOperation().Sync().Disabled().Handle(OnMerge);
            RegisterStep().OnGrantAccess().PostOperation().Sync().Disabled().Handle(OnGrantAccess);
            RegisterStep().OnModifyAccess().PostOperation().Sync().Disabled().Handle(OnModifyAccess);
            RegisterStep().OnRevokeAccess().PostOperation().Sync().Disabled().Handle(OnRevokeAccess);

            // Ownership reassignment (entity-bound).
            RegisterStep().OnAssign().PostOperation().Sync().Disabled().Handle(OnAssign);

            // Sharing reads (also valid on account).
            RegisterStep().OnRetrievePrincipalAccess().PostOperation().Sync().Disabled().Handle(OnRetrievePrincipalAccess);
            RegisterStep().OnRetrieveSharedPrincipalsAndAccess().PostOperation().Sync().Disabled().Handle(OnRetrieveSharedPrincipalsAndAccess);

            // Many more messages are modelled but belong to other tables, so they are not shown here on account:
            // e.g. QualifyLead (lead); WinOpportunity / LoseOpportunity / Generate*/Convert* (opportunity / quote /
            // order); AddToQueue / RouteTo (queue items); SendEmail / SendEmailFromTemplate (email); AddMembersTeam /
            // AddListMembersList (team / marketing list); CloseIncident (case); CancelContract / CloneContract
            // (contract). Register them the same way from the matching EntityPluginBase<T> with RegisterStep().On<Message>().
        }

        // ctx.Request.Target is the early-bound Account; ctx.Response.id is the new record id (Post step).
        private void OnCreate(ICreatePostContext<Account> ctx)
            => ctx.Trace("Create {0} -> id {1}", ctx.Request.Target.Name, ctx.Response.id);

        // ctx.Request.Target carries the columns that changed on this update.
        private void OnUpdate(IUpdatePostContext<Account> ctx)
            => ctx.Trace("Update {0}", ctx.Request.Target.Id);

        // Delete carries an EntityReference, exposed as ctx.Request.Target. This step is PreOperation, so the
        // handler receives the PRE context (ctx.Request but no ctx.Response - the record is not deleted yet).
        private void OnDelete(IDeletePreContext<Account> ctx)
            => ctx.Trace("Delete {0}", ctx.Request.Target.Id);

        // Bulk create: ctx.Request.Targets is the whole batch; ctx.Response.Ids are the new ids.
        private void OnCreateMultiple(ICreateMultiplePostContext<Account> ctx)
            => ctx.Trace("CreateMultiple of {0}", ctx.Request.Targets.Entities.Count);

        private void OnUpdateMultiple(IUpdateMultiplePostContext<Account> ctx)
            => ctx.Trace("UpdateMultiple of {0}", ctx.Request.Targets.Entities.Count);

        // Retrieve: ctx.Request.Target is the record read; ctx.Request.ColumnSet the requested columns.
        private void OnRetrieve(IRetrievePostContext<Account> ctx)
            => ctx.Trace("Retrieve {0} ({1} columns)", ctx.Request.Target.Id, ctx.Request.ColumnSet.Columns.Count);

        // RetrieveMultiple has no primary Target; the query is on ctx.Request.Query.
        private void OnRetrieveMultiple(IRetrieveMultiplePostContext<Account> ctx)
            => ctx.Trace("RetrieveMultiple, query type {0}", ctx.Request.Query.GetType().Name);

        // Associate / Disassociate: ctx.Request.Target plus ctx.Request.Relationship / RelatedEntities.
        private void OnAssociate(IAssociatePostContext<Account> ctx)
            => ctx.Trace("Associate {0} via {1}", ctx.Request.Target.Id, ctx.Request.Relationship.SchemaName);

        private void OnDisassociate(IDisassociatePostContext<Account> ctx)
            => ctx.Trace("Disassociate {0} via {1}", ctx.Request.Target.Id, ctx.Request.Relationship.SchemaName);

        // Merge: ctx.Request.Target is the master, ctx.Request.SubordinateId the record merged into it.
        private void OnMerge(IMergePostContext<Account> ctx)
            => ctx.Trace("Merge {0} into {1}", ctx.Request.SubordinateId, ctx.Request.Target.Id);

        // Sharing: ctx.Request.PrincipalAccess (grant/modify) and ctx.Request.Revokee (revoke).
        private void OnGrantAccess(IGrantAccessPostContext<Account> ctx)
            => ctx.Trace("Grant on {0} to {1}", ctx.Request.Target.Id, ctx.Request.PrincipalAccess.Principal.Id);

        private void OnModifyAccess(IModifyAccessPostContext<Account> ctx)
            => ctx.Trace("Modify access on {0}", ctx.Request.Target.Id);

        private void OnRevokeAccess(IRevokeAccessPostContext<Account> ctx)
            => ctx.Trace("Revoke on {0} from {1}", ctx.Request.Target.Id, ctx.Request.Revokee.Id);

        // Assign: ctx.Request.Target is the reassigned record; ctx.Request.Assignee the new owner.
        private void OnAssign(IAssignPostContext<Account> ctx)
            => ctx.Trace("Assign {0} to {1}", ctx.Request.Target.Id, ctx.Request.Assignee.Id);

        // Sharing reads: ctx.Response.AccessRights (one principal) / ctx.Response.PrincipalAccesses (all shares).
        private void OnRetrievePrincipalAccess(IRetrievePrincipalAccessPostContext<Account> ctx)
            => ctx.Trace("Access of {0} on {1}: {2}", ctx.Request.Principal.Id, ctx.Request.Target.Id, ctx.Response.AccessRights);

        private void OnRetrieveSharedPrincipalsAndAccess(IRetrieveSharedPrincipalsAndAccessPostContext<Account> ctx)
            => ctx.Trace("{0} share(s) on {1}", ctx.Response.PrincipalAccesses.Length, ctx.Request.Target.Id);
    }
}
""";

    public static string ContactRouterPlugin(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.Contact.Plugins
{
    using System;
    using Vela.Constants;
    using Vela.Context;
    using Vela.DI;
    using Vela.Plugin;
    using Microsoft.Xrm.Sdk;
    using {{prefix}}.Plugins.Contact.Services;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>
    /// Multi-step "router" plugin: registers several steps in one class and binds each to a named
    /// handler method. Every step in a multi-step plugin must declare a <c>Handler(...)</c>. Also shows a
    /// router consuming a table-scoped service (<see cref="IContactDuplicateService"/>) via DI.
    /// </summary>
    // partial: the typed image projections are generated as nested types inside this class (VELA014).
    public sealed partial class ContactRouterPlugin(string unsecureConfig, string secureConfig) : EntityPluginBase<Contact>(unsecureConfig, secureConfig)
    {
        protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context)
        {
            services.AddScoped<IContactDuplicateService>(r => new ContactDuplicateService(r.GetRequiredService<IOrganizationService>()));
        }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate()
                .PostOperation()
                .Sync()
                .FilterOn(c => c.FirstName, c => c.LastName, c => c.EMailAddress1)
                .WithDescription("Handles contact creation.")
                .WithPostImage(c => c.FirstName, c => c.LastName, c => c.EMailAddress1)
                .Handle(OnCreatePost);

            RegisterStep().OnUpdate()
                .PostOperation()
                .Sync()
                .FilterOn(c => c.LastName)
                .WithDescription("Handles contact last-name changes.")
                .WithPreImage(c => c.LastName)
                .Handle(OnUpdatePost);
        }

        // Each handler receives the message-typed context: ICreateContext exposes a CreateRequest, IUpdateContext
        // an UpdateRequest (in a Post step, a matching Response too), each strongly typed to this message. The
        // record is available already typed as ctx.Request.Target - no cast, no type argument. The context is still an
        // ILocalPluginContext, so images, services and tracing remain available.
        // Generated typed step context (OnCreatePostContext): ctx.PostImage is the captured post-image, typed to the
        // captured columns only (reading an uncaptured column is a compile error, not a silent null).
        private void OnCreatePost(OnCreatePostContext ctx)
        {
            var postImage = ctx.PostImage;
            ctx.Trace("Contact created: {0} {1}", postImage.FirstName, postImage.LastName);

            IContactDuplicateService duplicates = ctx.GetRequiredService<IContactDuplicateService>();
            Guid? duplicateId = duplicates.FindDuplicateByEmail(postImage.EMailAddress1, ctx.Request.Target.Id);
            if (duplicateId.HasValue)
            {
                ctx.Trace("Possible duplicate of contact {0} (same email).", duplicateId.Value);
            }
        }

        // Generated typed step context (OnUpdatePostContext): ctx.PreImage is the captured pre-image (LastName only).
        private void OnUpdatePost(OnUpdatePostContext ctx)
        {
            var preImage = ctx.PreImage;
            ctx.Trace("Contact last name changed from '{0}' to '{1}'", preImage.LastName, ctx.Request.Target.LastName);
        }
    }
}
""";

    public static string AccountEstimateValueCustomApi(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.Account.CustomApis
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Vela.Context;
    using Vela.CustomApi;
    using Vela.Messages;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>
    /// Custom API <b>bound</b> to the <c>account</c> table. The request/response derive from
    /// <see cref="TypedRequest"/>/<see cref="TypedResponse"/> so the same type serves authoring,
    /// calling (<c>service.Execute(new EstimateValueRequest { ... }).ToResponse&lt;EstimateValueResponse&gt;()</c>),
    /// and reacting.
    /// </summary>
    public sealed class AccountEstimateValueCustomApi(string unsecureConfig, string secureConfig) : CustomApiBase<EstimateValueRequest, EstimateValueResponse>(unsecureConfig, secureConfig)
    {
        protected override void RegisterCustomApi()
        {
            RegisterCustomApi("{{ApiPrefix(prefix)}}_AccountEstimateValue")
                .DisplayName("Estimate Account Value")
                .Description("Estimates a value for the bound account.")
                .AsAction()
                .BoundTo<Account>()
                .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.SyncAndAsync)
                .Request(r => r
                    .Add(x => x.AccountId)
                    .Add(x => x.AsOfDate, optional: true, "Date to value against; defaults to today."))
                .Response(r => r.Add(x => x.EstimatedValue));
        }

        protected override EstimateValueResponse Execute(ILocalPluginContext ctx, EstimateValueRequest request)
        {
            ctx.Trace("Estimating value for account {0}", request.AccountId);

            // Replace with real logic.
            return new EstimateValueResponse { EstimatedValue = 1000m };
        }
    }

    /// <summary>Request parameters for <see cref="AccountEstimateValueCustomApi"/>.</summary>
    public sealed class EstimateValueRequest : CustomApiRequest
    {
        public EstimateValueRequest() : base("{{ApiPrefix(prefix)}}_AccountEstimateValue")
        {
        }

        public Guid AccountId
        {
            get => Get<Guid>();
            set => Set(value);
        }

        // Optional request parameter (isoptional = true in Dataverse). Only request
        // parameters can be optional; response properties are always present.
        public DateTime AsOfDate
        {
            get => Get<DateTime>();
            set => Set(value);
        }
    }

    /// <summary>Response parameters for <see cref="AccountEstimateValueCustomApi"/>.</summary>
    public sealed class EstimateValueResponse : CustomApiResponse
    {
        public EstimateValueResponse() : base("{{ApiPrefix(prefix)}}_AccountEstimateValue")
        {
        }

        public decimal EstimatedValue
        {
            get => Get<decimal>();
            set => Set(value);
        }
    }
}
""";

    public static string ContactValidateCustomApi(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.Contact.CustomApis
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Vela.Context;
    using Vela.CustomApi;
    using Vela.Messages;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>
    /// Custom API <b>bound</b> to the <c>contact</c> table. The request/response derive from
    /// <see cref="TypedRequest"/>/<see cref="TypedResponse"/> so the same type serves authoring,
    /// calling (<c>service.Execute(new ContactValidateRequest { ... }).ToResponse&lt;ContactValidateResponse&gt;()</c>),
    /// and reacting.
    /// </summary>
    public sealed class ContactValidateCustomApi(string unsecureConfig, string secureConfig) : CustomApiBase<ContactValidateRequest, ContactValidateResponse>(unsecureConfig, secureConfig)
    {
        protected override void RegisterCustomApi()
        {
            RegisterCustomApi("{{ApiPrefix(prefix)}}_ContactValidate")
                .DisplayName("Validate Contact")
                .Description("Validates the bound contact's email address.")
                .AsAction()
                .BoundTo<Contact>()
                .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.SyncAndAsync)
                .Request(r => r.Add(x => x.ContactId))
                .Response(r => r.Add(x => x.IsValid));
        }

        protected override ContactValidateResponse Execute(ILocalPluginContext ctx, ContactValidateRequest request)
        {
            ctx.Trace("Validating contact {0}", request.ContactId);

            // Replace with real validation logic.
            return new ContactValidateResponse { IsValid = true };
        }
    }

    /// <summary>Request parameters for <see cref="ContactValidateCustomApi"/>.</summary>
    public sealed class ContactValidateRequest : CustomApiRequest
    {
        public ContactValidateRequest() : base("{{ApiPrefix(prefix)}}_ContactValidate")
        {
        }

        public Guid ContactId
        {
            get => Get<Guid>();
            set => Set(value);
        }
    }

    /// <summary>Response parameters for <see cref="ContactValidateCustomApi"/>.</summary>
    public sealed class ContactValidateResponse : CustomApiResponse
    {
        public ContactValidateResponse() : base("{{ApiPrefix(prefix)}}_ContactValidate")
        {
        }

        public bool IsValid
        {
            get => Get<bool>();
            set => Set(value);
        }
    }
}
""";

    public static string PingCustomApi(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.GlobalCustomApis
{
    using Vela.Context;
    using Vela.CustomApi;

    /// <summary>
    /// <b>Global</b> (unbound) Custom API using the lightweight style - no dedicated request/response
    /// models, parameters declared inline.
    /// </summary>
    public sealed class PingCustomApi(string unsecureConfig, string secureConfig) : CustomApiBase(unsecureConfig, secureConfig)
    {
        protected override void RegisterCustomApi()
        {
            RegisterCustomApi("{{ApiPrefix(prefix)}}_Ping", Handle)
                .DisplayName("Ping")
                .Description("Returns a pong message; useful for connectivity checks.")
                .AsAction()
                .RequestParameter<string>("Message", optional: true)
                .ResponseParameter<string>("Pong");
        }

        private void Handle(ILocalPluginContext ctx)
        {
            string message = ctx.GetInputParameter<string>("Message");
            ctx.SetOutputParameter("Pong", string.IsNullOrEmpty(message) ? "pong" : "pong: " + message);
        }
    }
}
""";

    // ---- Tests project ----------------------------------------------------

    public static string TestsCsproj(string testProjectName, string pluginsProjectName, string sharedProjectName, string coreVersion) =>
$$"""
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <!-- Plugins target net462, so their tests do too (Vela.Testing is a net462 package). -->
    <TargetFramework>net462</TargetFramework>
    <RootNamespace>{{testProjectName}}</RootNamespace>
    <AssemblyName>{{testProjectName}}</AssemblyName>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="{{TestSdkVersion}}" />
    <PackageReference Include="xunit" Version="{{XunitVersion}}" />
    <PackageReference Include="xunit.runner.visualstudio" Version="{{XunitRunnerVersion}}" />
    <!-- The test host runs against the real SDK assemblies (not the sandbox), so they are copied to output. -->
    <PackageReference Include="Microsoft.CrmSdk.CoreAssemblies" Version="{{CrmCoreAssembliesVersion}}" />
    <!-- Vela.Testing resolves from nuget.org, the same source as Vela.Plugins. -->
    <PackageReference Include="Vela.Testing" Version="{{coreVersion}}" />
  </ItemGroup>

  <ItemGroup>
    <!-- Test projects live under tests/; the projects under test are under src/. -->
    <ProjectReference Include="..\..\src\{{pluginsProjectName}}\{{pluginsProjectName}}.csproj" />
    <ProjectReference Include="..\..\src\{{sharedProjectName}}\{{sharedProjectName}}.csproj" />
  </ItemGroup>

</Project>
""";

    public static string SecurityExampleTests(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.Tests.Security
{
    using System;
    using System.ServiceModel;
    using Microsoft.Xrm.Sdk;
    using Microsoft.Xrm.Sdk.Query;
    using Vela.Testing;
    using Vela.Testing.Security;
    using Xunit;

    /// <summary>
    /// Example: exercising the harness's opt-in SECURITY MODEL. Security is OFF by default - with no
    /// <c>WithSecurity(...)</c> call the in-memory service grants full access (which is what the other tests rely
    /// on). Declare business units, named roles and users with <c>WithSecurity(...)</c>, pick who is acting with
    /// <c>WithCurrentUser(...)</c>, and the service then enforces table privileges on Create/Retrieve/Update/Delete
    /// and trims <c>RetrieveMultiple</c> to the records the caller may read - exactly like Dataverse. A denied
    /// operation throws <see cref="FaultException{OrganizationServiceFault}"/>, the same fault a live plugin sees.
    /// <c>Seed(...)</c> writes rows straight to the store (bypassing the checks) so you can arrange the world, then
    /// act as a specific user to assert what they can and cannot do.
    /// </summary>
    public sealed class SecurityExampleTests
    {
        private static readonly Guid SalesBusinessUnit = new Guid("00000000-0000-0000-0000-0000000b0001");
        private static readonly Guid Salesperson = new Guid("00000000-0000-0000-0000-0000000a0001");
        private static readonly Guid OutsideUser = new Guid("00000000-0000-0000-0000-0000000a0002");

        // A small scenario: one business unit and a "salesrep" role that can create/read/write accounts across the
        // whole organization. Salesperson holds it; OutsideUser holds no role at all (so: no access to anything).
        //
        // Roles are declared inline (by name) here so the example is self-contained. To mirror your REAL environment,
        // generate a dependency-light `Roles` class from its security roles with `vela models --roles` (or
        // `--solution-roles`) into `{{prefix}}.Shared/Models/Generated`, then configure the harness with the actual
        // privileges via `Role(Roles.SalesrepName, Roles.SalesrepId, Roles.Salesrep)` instead of the string name below.
        private static SecurityConfiguration Scenario(SecurityConfiguration security) => security
            .BusinessUnit(SalesBusinessUnit)
            .Role("salesrep", role => role.Grant("account", AccessLevel.Organization, Privilege.Create, Privilege.Read, Privilege.Write))
            .User(Salesperson, SalesBusinessUnit, "salesrep")
            .User(OutsideUser, SalesBusinessUnit);

        [Fact]
        public void A_user_with_the_role_can_create_and_read()
        {
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithCurrentUser(Salesperson)
                .WithSecurity(security => Scenario(security));

            Guid id = crm.Create(new Entity("account") { ["name"] = "Zava" }); // salesrep grants Create
            Entity account = crm.Retrieve("account", id, new ColumnSet(true));  // ...and Read

            Assert.Equal("Zava", account.GetAttributeValue<string>("name"));
        }

        [Fact]
        public void A_user_without_any_privilege_is_denied()
        {
            Guid id = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithCurrentUser(OutsideUser)
                .WithSecurity(security => Scenario(security))
                .Seed(new Entity("account", id) { ["name"] = "Zava" }); // Seed bypasses security to arrange the row

            // OutsideUser has no Read privilege on account, so Retrieve throws the platform fault a plugin would see.
            Assert.Throws<FaultException<OrganizationServiceFault>>(
                () => crm.Retrieve("account", id, new ColumnSet(true)));
        }

        [Fact]
        public void RetrieveMultiple_returns_only_the_records_the_user_can_read()
        {
            Guid mine = Guid.NewGuid();
            Guid theirs = Guid.NewGuid();
            InMemoryOrganizationService crm = new InMemoryOrganizationService()
                .WithCurrentUser(Salesperson)
                .WithSecurity(security => security
                    .BusinessUnit(SalesBusinessUnit)
                    // User-level Read: the caller sees only records they own, not the whole table.
                    .Role("ownrecords", role => role.Grant("account", AccessLevel.User, Privilege.Read))
                    .User(Salesperson, SalesBusinessUnit, "ownrecords")
                    .User(OutsideUser, SalesBusinessUnit, "ownrecords"))
                .Seed(
                    new Entity("account", mine) { ["name"] = "Mine", ["ownerid"] = new EntityReference("systemuser", Salesperson) },
                    new Entity("account", theirs) { ["name"] = "Theirs", ["ownerid"] = new EntityReference("systemuser", OutsideUser) });

            EntityCollection result = crm.RetrieveMultiple(new QueryExpression("account") { ColumnSet = new ColumnSet(true) });

            Entity onlyMine = Assert.Single(result.Entities); // User-level Read trims the record owned by someone else
            Assert.Equal(mine, onlyMine.Id);
        }
    }
}
""";

    public static string AccountPostCreatePluginTests(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.Tests.Account.Plugins
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Microsoft.Xrm.Sdk.Query;
    using Vela.Testing;
    using Xunit;
    using {{prefix}}.Plugins.Account.Plugins;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>
    /// Tests <see cref="AccountPostCreatePlugin"/> with <c>Vela.Testing</c>: an in-memory Dataverse plus a
    /// fake execution context - no connection, no mocking library. Plugins are constructed with their
    /// <c>(unsecureConfig, secureConfig)</c> constructor and run via <c>.Execute(plugin)</c>.
    /// </summary>
    public sealed class AccountPostCreatePluginTests
    {
        [Fact]
        public void Schedules_A_Follow_Up_Task_For_The_New_Account()
        {
            // Arrange: an in-memory Dataverse the plugin's injected IOrganizationService writes to.
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Guid accountId = Guid.NewGuid();
            Account account = new Account { Id = accountId, Name = "Zava" };

            // Act: run the plugin. The trigger (Create/PostOperation/Synchronous) is auto-derived from the
            // plugin's own single registered step, so there is no need to restate OnCreate().Stage(...).Mode(...).
            PluginContext.For<Account>()
                .WithPrimaryEntity(Account.EntityLogicalNameValue, accountId)
                .WithTarget(account)
                .WithPostImage(account)
                .WithOrganizationService(crm)
                .Execute(new AccountPostCreatePlugin(string.Empty, string.Empty));

            // Assert: retrieve the created task and check its fields (not just the count).
            EntityCollection tasks = crm.RetrieveMultiple(
                new QueryExpression("task") { ColumnSet = new ColumnSet(true) });
            Entity task = Assert.Single(tasks.Entities);
            Assert.Equal("Follow up with Zava", task.GetAttributeValue<string>("subject"));

            EntityReference regarding = task.GetAttributeValue<EntityReference>("regardingobjectid");
            Assert.Equal("account", regarding.LogicalName);
            Assert.Equal(accountId, regarding.Id);
        }
    }
}
""";

    public static string ContactRouterPluginTests(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.Tests.Contact.Plugins
{
    using System;
    using Vela.Constants;
    using Vela.Steps;
    using Vela.Testing;
    using Xunit;
    using {{prefix}}.Plugins.Contact.Plugins;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>
    /// Tests the multi-step "router" <see cref="ContactRouterPlugin"/>. Because a router registers several
    /// steps, the trigger (message/stage/mode) must be stated explicitly so the right handler runs. Asserts
    /// on the emitted traces via <c>TraceCapture</c>.
    /// </summary>
    public sealed class ContactRouterPluginTests
    {
        [Fact]
        public void OnCreate_Flags_A_Possible_Duplicate_With_The_Same_Email()
        {
            // Seed an existing contact that shares the email of the one being created.
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            Guid existingId = crm.Create(new Contact
            {
                FirstName = "Jane",
                LastName = "Doe",
                EMailAddress1 = "jane@zava.com",
            });

            Guid newContactId = Guid.NewGuid();
            Contact created = new Contact
            {
                Id = newContactId,
                FirstName = "Janet",
                LastName = "Doe",
                EMailAddress1 = "jane@zava.com",
            };

            PluginContextBuilder<Contact> builder = PluginContext.For<Contact>()
                .OnCreate().Stage(PluginStages.PostOperation).Sync()
                .WithPrimaryEntity(Contact.EntityLogicalNameValue, newContactId)
                .WithTarget(created)
                .WithPostImage(created)
                .WithOrganizationService(crm);

            builder.Execute(new ContactRouterPlugin(string.Empty, string.Empty));

            Assert.Contains("Contact created: Janet Doe", builder.TraceCapture.Output);
            Assert.Contains("Possible duplicate of contact " + existingId, builder.TraceCapture.Output);
        }
    }
}
""";

    public static string AccountEstimateValueCustomApiTests(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.Tests.Account.CustomApis
{
    using System;
    using Vela.Context;
    using Vela.Testing;
    using Xunit;
    using {{prefix}}.Plugins.Account.CustomApis;

    /// <summary>
    /// Tests the typed, account-bound Custom API <see cref="AccountEstimateValueCustomApi"/>. The framework
    /// maps input parameters to the request model and the response model back to output parameters.
    /// </summary>
    public sealed class AccountEstimateValueCustomApiTests
    {
        [Fact]
        public void Returns_An_Estimated_Value_For_The_Bound_Account()
        {
            AccountEstimateValueCustomApi api = new AccountEstimateValueCustomApi(string.Empty, string.Empty);
            LocalPluginContext context = PluginContext.ForMessage("{{ApiPrefix(prefix)}}_AccountEstimateValue").Build();
            context.PluginExecutionContext.InputParameters["AccountId"] = Guid.NewGuid();

            api.Execute(context.ServiceProvider);

            Assert.Equal(1000m, context.PluginExecutionContext.OutputParameters["EstimatedValue"]);
        }
    }
}
""";

    public static string ContactValidateCustomApiTests(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.Tests.Contact.CustomApis
{
    using System;
    using Vela.Context;
    using Vela.Testing;
    using Xunit;
    using {{prefix}}.Plugins.Contact.CustomApis;

    /// <summary>
    /// Tests the typed, contact-bound Custom API <see cref="ContactValidateCustomApi"/>. The framework maps
    /// input parameters to the request model and the response model back to output parameters.
    /// </summary>
    public sealed class ContactValidateCustomApiTests
    {
        [Fact]
        public void Reports_The_Bound_Contact_As_Valid()
        {
            ContactValidateCustomApi api = new ContactValidateCustomApi(string.Empty, string.Empty);
            LocalPluginContext context = PluginContext.ForMessage("{{ApiPrefix(prefix)}}_ContactValidate").Build();
            context.PluginExecutionContext.InputParameters["ContactId"] = Guid.NewGuid();

            api.Execute(context.ServiceProvider);

            Assert.Equal(true, context.PluginExecutionContext.OutputParameters["IsValid"]);
        }
    }
}
""";

    public static string PingCustomApiTests(string prefix) =>
$$"""
namespace {{prefix}}.Plugins.Tests.GlobalCustomApis
{
    using Vela.Context;
    using Vela.Testing;
    using Xunit;
    using {{prefix}}.Plugins.GlobalCustomApis;

    /// <summary>
    /// Tests the global (unbound), lightweight Custom API <see cref="PingCustomApi"/>: input parameters are
    /// read with <c>ctx.GetInputParameter</c> and results written with <c>ctx.SetOutputParameter</c>.
    /// </summary>
    public sealed class PingCustomApiTests
    {
        [Fact]
        public void Returns_Pong_With_The_Supplied_Message()
        {
            PingCustomApi api = new PingCustomApi(string.Empty, string.Empty);
            LocalPluginContext context = PluginContext.ForMessage("{{ApiPrefix(prefix)}}_Ping").Build();
            context.PluginExecutionContext.InputParameters["Message"] = "hello";

            api.Execute(context.ServiceProvider);

            Assert.Equal("pong: hello", context.PluginExecutionContext.OutputParameters["Pong"]);
        }
    }
}
""";

    // ---- Parameterized single-file templates (vela add) ------------------
    // These differ from the example templates above: they emit ONE file with a user-supplied name into an
    // existing project, following the same by-table conventions vela init uses.

    public static string AddPluginFile(string ns, string className, string entityType, string modelsNamespace, string message, string registrationCall, string stageCall = ".PostOperation()", string modeCall = ".Sync()", string filteringCall = "", string handlerContextType = "ILocalPluginContext") =>
$$"""
namespace {{ns}}
{
    using Vela.Constants;
    using Vela.Context;
    using Vela.Plugin;
    using {{modelsNamespace}};

    /// <summary>Plugin generated by <c>vela add plugin</c>.</summary>
    public sealed partial class {{className}}(string unsecureConfig, string secureConfig) : EntityPluginBase<{{entityType}}>(unsecureConfig, secureConfig)
    {
        protected override void RegisterPluginSteps()
        {
            RegisterStep().{{registrationCall}}
                {{stageCall}}
                {{modeCall}}{{filteringCall}}
                .WithDescription("{{message}} {{entityType}} handler.")
                .Handle(OnExecute);
        }

        private void OnExecute({{handlerContextType}} ctx)
        {
            ctx.Trace("{{className}} executed for {0} '{1}'.", ctx.PrimaryEntityName, ctx.PrimaryEntityId);

            // TODO: implement your logic. For a Create/Update message, read the typed record with
            // ctx.Request.Target; for a *Multiple message use ctx.Request.Targets.
        }
    }
}
""";

    public static string AddServiceFile(string ns, string interfaceName, string className, string entityType, string modelsNamespace) =>
$$"""
namespace {{ns}}
{
    using System;
    using Microsoft.Xrm.Sdk;
    using {{modelsNamespace}};

    /// <summary>
    /// Table-scoped domain service generated by <c>vela add service</c>. Following the by-table
    /// convention of <c>vela init</c>, it lives next to the <c>{{entityType}}</c> plugins that use it.
    /// Register it from a plugin's <c>ConfigureServices(...)</c> and resolve it with
    /// <c>ctx.GetRequiredService&lt;{{interfaceName}}&gt;()</c>.
    /// </summary>
    public interface {{interfaceName}}
    {
        void Execute({{entityType}} target);
    }

    /// <inheritdoc />
    public sealed class {{className}}(IOrganizationService service) : {{interfaceName}}
    {
        private readonly IOrganizationService _service = service;

        public void Execute({{entityType}} target)
        {
            // TODO: implement your domain logic using _service.
        }
    }
}
""";

    public static string AddPluginTestFile(string ns, string pluginNamespace, string modelsNamespace, string testClass, string pluginType, string entityType, bool multiple = false)
    {
        // A *Multiple plugin reads ctx.Request.Targets (an EntityCollection), so the test must seed Targets via
        // WithTargets(...); a single-record plugin uses WithTarget(...). Emitting the wrong one leaves
        // ctx.Request.Targets null and the handler throws.
        string targetMethod = multiple ? "WithTargets" : "WithTarget";
        string targetComment = multiple
            ? "a *Multiple plugin reads ctx.Request.Targets; seed it with WithTargets(...)."
            : "a single-step plugin auto-derives its trigger (message/stage/mode) from its own";
        return
$$"""
namespace {{ns}}
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Microsoft.Xrm.Sdk.Query;
    using Vela.Testing;
    using Xunit;
    using {{pluginNamespace}};
    using {{modelsNamespace}};

    /// <summary>
    /// Test for <see cref="{{pluginType}}"/>, generated by <c>vela add test</c>. Uses <c>Vela.Testing</c>'s
    /// in-memory Dataverse and a fake execution context - no connection, no mocking library.
    /// </summary>
    public sealed class {{testClass}}
    {
        [Fact]
        public void Executes()
        {
            // Arrange: an in-memory Dataverse the plugin's injected IOrganizationService writes to.
            InMemoryOrganizationService crm = new InMemoryOrganizationService();
            {{entityType}} target = new {{entityType}} { Id = Guid.NewGuid() };

            // Act: {{targetComment}}
            // For a multi-step plugin, state the trigger: .OnUpdate().Stage(...).Mode(...).
            PluginContext.For<{{entityType}}>()
                .{{targetMethod}}(target)
                .WithOrganizationService(crm)
                .Execute(new {{pluginType}}(string.Empty, string.Empty));

            // Assert: retrieve the affected record(s) and check their fields (not just the count). For example:
            // EntityCollection rows = crm.RetrieveMultiple(new QueryExpression("task") { ColumnSet = new ColumnSet(true) });
            // Entity row = Assert.Single(rows.Entities);
            // Assert.Equal("expected", row.GetAttributeValue<string>("subject"));
        }
    }
}
""";
    }

    public static string AddCustomApiTestFile(string ns, string apiNamespace, string testClass, string apiType, string uniqueName) =>
$$"""
namespace {{ns}}
{
    using System;
    using Vela.Context;
    using Vela.Testing;
    using Xunit;
    using {{apiNamespace}};

    /// <summary>
    /// Test for <see cref="{{apiType}}"/>, generated by <c>vela add test</c>. The framework maps input
    /// parameters to the request and the response back to output parameters.
    /// </summary>
    public sealed class {{testClass}}
    {
        [Fact]
        public void Executes()
        {
            {{apiType}} api = new {{apiType}}(string.Empty, string.Empty);
            LocalPluginContext context = PluginContext.ForMessage("{{uniqueName}}").Build();

            // TODO: set the input parameters this Custom API reads, e.g.:
            // context.PluginExecutionContext.InputParameters["AccountId"] = Guid.NewGuid();

            api.Execute(context.ServiceProvider);

            // TODO: assert the output parameters it writes, e.g.:
            // Assert.Equal(1000m, context.PluginExecutionContext.OutputParameters["EstimatedValue"]);
        }
    }
}
""";

    public static string AddGlobalCustomApiFile(string ns, string className, string uniqueName, string operation) =>
$$"""
namespace {{ns}}
{
    using Vela.Context;
    using Vela.CustomApi;

    /// <summary>Global (unbound), lightweight Custom API generated by <c>vela add customapi</c>.</summary>
    public sealed class {{className}}(string unsecureConfig, string secureConfig) : CustomApiBase(unsecureConfig, secureConfig)
    {
        protected override void RegisterCustomApi()
        {
            RegisterCustomApi("{{uniqueName}}", Handle)
                .DisplayName("{{className}}")
                .Description("TODO: describe this Custom API.")
                .{{operation}};
        }

        private void Handle(ILocalPluginContext ctx)
        {
            // TODO: read inputs with ctx.GetInputParameter<T>(...) and write results with ctx.SetOutputParameter(...).
        }
    }
}
""";

    public static string AddBoundCustomApiFile(string ns, string className, string uniqueName, string entityType, string modelsNamespace, string operation) =>
$$"""
namespace {{ns}}
{
    using Microsoft.Xrm.Sdk;
    using Vela.Context;
    using Vela.CustomApi;
    using Vela.Messages;
    using {{modelsNamespace}};

    /// <summary>Typed Custom API bound to <c>{{entityType}}</c>, generated by <c>vela add customapi</c>.</summary>
    public sealed class {{className}}(string unsecureConfig, string secureConfig) : CustomApiBase<{{className}}Request, {{className}}Response>(unsecureConfig, secureConfig)
    {
        protected override void RegisterCustomApi()
        {
            RegisterCustomApi("{{uniqueName}}")
                .DisplayName("{{className}}")
                .Description("TODO: describe this Custom API.")
                .{{operation}}
                .BoundTo<{{entityType}}>()
                .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.SyncAndAsync);
        }

        protected override {{className}}Response Execute(ILocalPluginContext ctx, {{className}}Request request)
        {
            // TODO: implement your logic. Add typed properties (over Parameters/Results) to the request/response
            // classes below and map them with .Request(r => r.Add(x => x.Foo)) / .Response(r => r.Add(x => x.Bar)).
            return new {{className}}Response();
        }
    }

    /// <summary>
    /// Request for <see cref="{{className}}"/>. Derives from <see cref="CustomApiRequest"/> so each property keys
    /// the Parameters bag by its own plain name (for example a <c>RecordId</c> property reads/writes
    /// <c>Parameters["RecordId"]</c>) - the registered parameter names have no dot or prefix. The
    /// same type serves authoring, calling (<c>service.Execute(new {{className}}Request { ... }).ToResponse&lt;{{className}}Response&gt;()</c>),
    /// and reacting. Add typed properties over the base <c>Get&lt;T&gt;()</c> / <c>Set(value)</c> helpers, for example:
    /// <code>
    /// public Guid RecordId
    /// {
    ///     get => Get&lt;Guid&gt;();
    ///     set => Set(value);
    /// }
    /// </code>
    /// </summary>
    public sealed class {{className}}Request : CustomApiRequest
    {
        public {{className}}Request() : base("{{uniqueName}}")
        {
        }
    }

    /// <summary>
    /// Response for <see cref="{{className}}"/>. Derives from <see cref="CustomApiResponse"/> so each property keys
    /// the Results bag by its own plain name (for example a <c>Status</c> property reads/writes
    /// <c>Results["Status"]</c>) - the registered output names have no dot or prefix. Add typed properties over the base <c>Get&lt;T&gt;()</c> /
    /// <c>Set(value)</c> helpers, for example:
    /// <code>
    /// public string Status
    /// {
    ///     get => Get&lt;string&gt;();
    ///     set => Set(value);
    /// }
    /// </code>
    /// </summary>
    public sealed class {{className}}Response : CustomApiResponse
    {
        public {{className}}Response() : base("{{uniqueName}}")
        {
        }
    }
}
""";

    // ---- Plugin code (Integrations project) ------------------------------

    public static string ContactSyncPlugin(string prefix) =>
$$"""
namespace {{prefix}}.Integrations.Contact.Plugins
{
    using Vela.Constants;
    using Vela.Context;
    using Vela.DI;
    using Vela.Plugin;
    using {{prefix}}.Shared.Models.Generated;
    using {{prefix}}.Shared.Services;

    /// <summary>
    /// Single-step integration plugin: runs asynchronously after a contact is created. Demonstrates
    /// dependency injection of the cross-table <see cref="IGreetingService"/> in a second plugin package.
    /// </summary>
    // partial: capturing a typed image (WithPostImage selectors) generates a nested projection type (VELA014).
    public sealed partial class ContactSyncPlugin(string unsecureConfig, string secureConfig) : EntityPluginBase<Contact>(unsecureConfig, secureConfig)
    {
        protected override void ConfigureServices(SimpleServiceResolver services, ILocalPluginContext context)
        {
            services.AddScoped<IGreetingService>(_ => new GreetingService());
        }

        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate()
                .PostOperation()
                .Async()
                .FilterOn(c => c.FirstName, c => c.LastName, c => c.EMailAddress1)
                .WithDescription("Runs after a contact is created.")
                .WithPostImage(c => c.FirstName, c => c.LastName, c => c.EMailAddress1)
                .Handle(OnContactCreated);
        }

        private void OnContactCreated(ICreatePostContext<Contact> ctx)
        {
            Contact target = ctx.GetTarget<Contact>();
            IGreetingService greetings = ctx.GetRequiredService<IGreetingService>();
            ctx.Trace(greetings.BuildGreeting(target.FirstName));
        }
    }
}
""";

    public static string AccountSyncRouterPlugin(string prefix) =>
$$"""
namespace {{prefix}}.Integrations.Account.Plugins
{
    using Vela.Constants;
    using Vela.Context;
    using Vela.Plugin;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>
    /// Multi-step integration router: one class handling create and update of accounts, each bound to
    /// a dedicated handler method.
    /// </summary>
    // partial: the typed image projections are generated as nested types inside this class (VELA014).
    public sealed partial class AccountSyncRouterPlugin(string unsecureConfig, string secureConfig) : EntityPluginBase<Account>(unsecureConfig, secureConfig)
    {
        protected override void RegisterPluginSteps()
        {
            RegisterStep().OnCreate()
                .PostOperation()
                .Async()
                .FilterOn(a => a.Name)
                .WithDescription("Pushes a newly created account downstream.")
                .WithPostImage(a => a.Name)
                .Handle(OnAccountCreated);

            RegisterStep().OnUpdate()
                .PostOperation()
                .Async()
                .FilterOn(a => a.Name, a => a.Telephone1)
                .WithDescription("Pushes account changes downstream.")
                .WithPreImage(a => a.Name, a => a.Telephone1)
                .Handle(OnAccountChanged);
        }

        // Generated typed step context (OnAccountCreatedContext): ctx.PostImage is the captured post-image (Name only).
        private void OnAccountCreated(OnAccountCreatedContext ctx)
        {
            var postImage = ctx.PostImage;
            ctx.Trace("Sync: account created '{0}'", postImage.Name);
        }

        // Generated typed step context (OnAccountChangedContext): ctx.PreImage is the captured pre-image (Name + Telephone1).
        private void OnAccountChanged(OnAccountChangedContext ctx)
        {
            var preImage = ctx.PreImage;
            ctx.Trace("Sync: account '{0}' changed", preImage.Name);
        }
    }
}
""";

    public static string ContactExportCustomApi(string prefix) =>
$$"""
namespace {{prefix}}.Integrations.Contact.CustomApis
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Vela.Context;
    using Vela.CustomApi;
    using Vela.Messages;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>Custom API <b>bound</b> to the <c>contact</c> table: exports a contact to an external system.</summary>
    public sealed class ContactExportCustomApi(string unsecureConfig, string secureConfig) : CustomApiBase<ContactExportRequest, ContactExportResponse>(unsecureConfig, secureConfig)
    {
        protected override void RegisterCustomApi()
        {
            RegisterCustomApi("{{ApiPrefix(prefix)}}_ContactExport")
                .DisplayName("Export Contact")
                .Description("Exports the bound contact to an external system.")
                .AsAction()
                .BoundTo<Contact>()
                .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.SyncAndAsync)
                .Request(r => r.Add(x => x.ContactId))
                .Response(r => r.Add(x => x.ExternalId));
        }

        protected override ContactExportResponse Execute(ILocalPluginContext ctx, ContactExportRequest request)
        {
            ctx.Trace("Exporting contact {0}", request.ContactId);

            // Replace with real integration logic.
            return new ContactExportResponse { ExternalId = request.ContactId.ToString() };
        }
    }

    /// <summary>Request parameters for <see cref="ContactExportCustomApi"/>.</summary>
    public sealed class ContactExportRequest : CustomApiRequest
    {
        public ContactExportRequest() : base("{{ApiPrefix(prefix)}}_ContactExport")
        {
        }

        public Guid ContactId
        {
            get => Get<Guid>();
            set => Set(value);
        }
    }

    /// <summary>Response parameters for <see cref="ContactExportCustomApi"/>.</summary>
    public sealed class ContactExportResponse : CustomApiResponse
    {
        public ContactExportResponse() : base("{{ApiPrefix(prefix)}}_ContactExport")
        {
        }

        public string ExternalId
        {
            get => Get<string>();
            set => Set(value);
        }
    }
}
""";

    public static string AccountReconcileCustomApi(string prefix) =>
$$"""
namespace {{prefix}}.Integrations.Account.CustomApis
{
    using System;
    using Microsoft.Xrm.Sdk;
    using Vela.Context;
    using Vela.CustomApi;
    using Vela.Messages;
    using {{prefix}}.Shared.Models.Generated;

    /// <summary>Custom API <b>bound</b> to the <c>account</c> table: reconciles the account with an external system.</summary>
    public sealed class AccountReconcileCustomApi(string unsecureConfig, string secureConfig) : CustomApiBase<AccountReconcileRequest, AccountReconcileResponse>(unsecureConfig, secureConfig)
    {
        protected override void RegisterCustomApi()
        {
            RegisterCustomApi("{{ApiPrefix(prefix)}}_AccountReconcile")
                .DisplayName("Reconcile Account")
                .Description("Reconciles the bound account with an external system.")
                .AsAction()
                .BoundTo<Account>()
                .AllowedCustomProcessingSteps(CustomApiAllowedCustomProcessingStepType.SyncAndAsync)
                .Request(r => r.Add(x => x.AccountId))
                .Response(r => r.Add(x => x.Status));
        }

        protected override AccountReconcileResponse Execute(ILocalPluginContext ctx, AccountReconcileRequest request)
        {
            ctx.Trace("Reconciling account {0}", request.AccountId);

            // Replace with real integration logic.
            return new AccountReconcileResponse { Status = "Reconciled" };
        }
    }

    /// <summary>Request parameters for <see cref="AccountReconcileCustomApi"/>.</summary>
    public sealed class AccountReconcileRequest : CustomApiRequest
    {
        public AccountReconcileRequest() : base("{{ApiPrefix(prefix)}}_AccountReconcile")
        {
        }

        public Guid AccountId
        {
            get => Get<Guid>();
            set => Set(value);
        }
    }

    /// <summary>Response parameters for <see cref="AccountReconcileCustomApi"/>.</summary>
    public sealed class AccountReconcileResponse : CustomApiResponse
    {
        public AccountReconcileResponse() : base("{{ApiPrefix(prefix)}}_AccountReconcile")
        {
        }

        public string Status
        {
            get => Get<string>();
            set => Set(value);
        }
    }
}
""";

    public static string IntegrationHeartbeatCustomApi(string prefix) =>
$$"""
namespace {{prefix}}.Integrations.GlobalCustomApis
{
    using Vela.Context;
    using Vela.CustomApi;

    /// <summary><b>Global</b> (unbound) Custom API that reports integration health.</summary>
    public sealed class IntegrationHeartbeatCustomApi(string unsecureConfig, string secureConfig) : CustomApiBase(unsecureConfig, secureConfig)
    {
        protected override void RegisterCustomApi()
        {
            RegisterCustomApi("{{ApiPrefix(prefix)}}_IntegrationHeartbeat", Handle)
                .DisplayName("Integration Heartbeat")
                .Description("Returns the integration status.")
                .AsFunction()
                .ResponseParameter<string>("Status");
        }

        private void Handle(ILocalPluginContext ctx)
        {
            ctx.SetOutputParameter("Status", "healthy");
        }
    }
}
""";

    public static string AccountReconcileCustomApiTests(string prefix) =>
$$"""
namespace {{prefix}}.Integrations.Tests.Account.CustomApis
{
    using System;
    using Vela.Context;
    using Vela.Testing;
    using Xunit;
    using {{prefix}}.Integrations.Account.CustomApis;

    /// <summary>Tests the typed, account-bound Custom API <see cref="AccountReconcileCustomApi"/>.</summary>
    public sealed class AccountReconcileCustomApiTests
    {
        [Fact]
        public void Returns_A_Reconciled_Status_For_The_Bound_Account()
        {
            AccountReconcileCustomApi api = new AccountReconcileCustomApi(string.Empty, string.Empty);
            LocalPluginContext context = PluginContext.ForMessage("{{ApiPrefix(prefix)}}_AccountReconcile").Build();
            context.PluginExecutionContext.InputParameters["AccountId"] = Guid.NewGuid();

            api.Execute(context.ServiceProvider);

            Assert.Equal("Reconciled", context.PluginExecutionContext.OutputParameters["Status"]);
        }
    }
}
""";

    public static string ContactExportCustomApiTests(string prefix) =>
$$"""
namespace {{prefix}}.Integrations.Tests.Contact.CustomApis
{
    using System;
    using Vela.Context;
    using Vela.Testing;
    using Xunit;
    using {{prefix}}.Integrations.Contact.CustomApis;

    /// <summary>Tests the typed, contact-bound Custom API <see cref="ContactExportCustomApi"/>.</summary>
    public sealed class ContactExportCustomApiTests
    {
        [Fact]
        public void Returns_The_Contact_Id_As_External_Id()
        {
            ContactExportCustomApi api = new ContactExportCustomApi(string.Empty, string.Empty);
            Guid contactId = Guid.NewGuid();
            LocalPluginContext context = PluginContext.ForMessage("{{ApiPrefix(prefix)}}_ContactExport").Build();
            context.PluginExecutionContext.InputParameters["ContactId"] = contactId;

            api.Execute(context.ServiceProvider);

            Assert.Equal(contactId.ToString(), context.PluginExecutionContext.OutputParameters["ExternalId"]);
        }
    }
}
""";

    public static string IntegrationHeartbeatCustomApiTests(string prefix) =>
$$"""
namespace {{prefix}}.Integrations.Tests.GlobalCustomApis
{
    using Vela.Context;
    using Vela.Testing;
    using Xunit;
    using {{prefix}}.Integrations.GlobalCustomApis;

    /// <summary>Tests the global (unbound), lightweight Custom API <see cref="IntegrationHeartbeatCustomApi"/>.</summary>
    public sealed class IntegrationHeartbeatCustomApiTests
    {
        [Fact]
        public void Reports_A_Healthy_Status()
        {
            IntegrationHeartbeatCustomApi api = new IntegrationHeartbeatCustomApi(string.Empty, string.Empty);
            LocalPluginContext context = PluginContext.ForMessage("{{ApiPrefix(prefix)}}_IntegrationHeartbeat").Build();

            api.Execute(context.ServiceProvider);

            Assert.Equal("healthy", context.PluginExecutionContext.OutputParameters["Status"]);
        }
    }
}
""";

    // ---- ConsoleApp project ----------------------------------------------

    public static string ConsoleCsproj(ScaffoldOptions options) =>
$$"""
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>{{options.Prefix}}.ConsoleApp</RootNamespace>
    <AssemblyName>{{options.Prefix}}.ConsoleApp</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RollForward>Major</RollForward>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.PowerPlatform.Dataverse.Client" Version="{{DataverseClientVersion}}" />
    <!-- Pin the patched System.Security.Cryptography.Xml over the client's advisory transitive version (NU1903). -->
    <PackageReference Include="System.Security.Cryptography.Xml" Version="{{CryptographyXmlVersion}}" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\{{options.Prefix}}.Shared\{{options.Prefix}}.Shared.csproj" />
  </ItemGroup>

</Project>
""";

    public static string ConsoleProgram(string prefix) =>
$$"""
using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using {{prefix}}.Shared.Models.Generated;

// Interactive (browser) sign-in to Dataverse. Pass the environment URL as the first argument,
// or enter it when prompted; a browser window opens for Microsoft login.
string? url = args.Length > 0 ? args[0] : null;
if (string.IsNullOrWhiteSpace(url))
{
    Console.Write("Dataverse environment URL (https://org.crm.dynamics.com): ");
    url = Console.ReadLine();
}

if (string.IsNullOrWhiteSpace(url))
{
    Console.Error.WriteLine("An environment URL is required.");
    return 1;
}

// Well-known Microsoft public client registered for interactive Dataverse sign-in.
// The redirect URI must be loopback: MSAL's system-browser flow rejects non-loopback redirects.
const string publicAppId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
const string redirectUri = "http://localhost";
string connectionString =
    $"AuthType=OAuth;Url={url};AppId={publicAppId};RedirectUri={redirectUri};LoginPrompt=Auto";

using var service = new ServiceClient(connectionString);
if (!service.IsReady)
{
    Console.Error.WriteLine($"Failed to connect: {service.LastError}");
    return 1;
}

var whoAmI = (WhoAmIResponse)service.Execute(new WhoAmIRequest());
Console.WriteLine($"Connected as user {whoAmI.UserId}.");

// Retrieve the five most recent accounts using the shared early-bound model.
var query = new QueryExpression(Account.EntityLogicalNameValue)
{
    ColumnSet = new ColumnSet("name"),
    TopCount = 5,
};

EntityCollection result = service.RetrieveMultiple(query);
Console.WriteLine($"Found {result.Entities.Count} account(s):");
foreach (Account account in result.Entities.Select(e => e.ToEntity<Account>()))
{
    Console.WriteLine($"  - {account.Name}");
}

return 0;
""";
}
