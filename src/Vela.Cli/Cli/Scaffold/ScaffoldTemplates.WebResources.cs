namespace Vela.Cli;

/// <summary>Templates for the opt-in TypeScript web-resources project (preview).</summary>
internal static partial class ScaffoldTemplates
{
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
}
