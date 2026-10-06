using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Xrm.Sdk.Metadata;
using Vela.Cli;
using Vela.Cli.Tests.TestSupport;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// Opt-in TypeScript compile smoke: emits a form model that spans every FieldModel kind and type-checks it against
/// the real vela-client <c>FormModel</c> type with <c>tsc --noEmit</c> (strict). Off by default; enable with
/// <c>VELA_TS_SMOKE=1</c> and point <c>VELA_CLIENT_DIR</c> at the vela-client package root (the folder whose
/// <c>src/form/model.ts</c> defines FormModel and whose <c>node_modules</c> has TypeScript installed). This is the
/// analog of a build smoke: it proves the generator output is not just structurally right but assignable to the
/// runtime contract.
/// </summary>
public class TsCompileSmokeTests
{
    // A single form exercising every FieldModel kind, so a successful compile covers the whole union.
    private const string SampleFormXml = """
        <form><tabs><tab name="general">
          <labels><label description="General" languagecode="1033" /></labels>
          <columns><column width="100%"><sections><section name="all">
            <labels><label description="All" languagecode="1033" /></labels>
            <rows>
              <row><cell><control datafieldname="name" /></cell></row>
              <row><cell><control datafieldname="description" /></cell></row>
              <row><cell><control datafieldname="count" /></cell></row>
              <row><cell><control datafieldname="bignum" /></cell></row>
              <row><cell><control datafieldname="ratio" /></cell></row>
              <row><cell><control datafieldname="score" /></cell></row>
              <row><cell><control datafieldname="revenue" /></cell></row>
              <row><cell><control datafieldname="flag" /></cell></row>
              <row><cell><control datafieldname="when" /></cell></row>
              <row><cell><control datafieldname="color" /></cell></row>
              <row><cell><control datafieldname="tags" /></cell></row>
              <row><cell><control datafieldname="ownerid" /></cell></row>
              <row><cell><control datafieldname="attendees" /></cell></row>
              <row><cell><control datafieldname="photo" /></cell></row>
              <row><cell><control datafieldname="contract" /></cell></row>
              <row><cell><control datafieldname="sampleid" /></cell></row>
              <row><cell>
                <control id="Related" classid="{E7A81278-8635-4d9e-8D4D-59480B391C5B}">
                  <parameters>
                    <TargetEntityType>contact</TargetEntityType>
                    <RelationshipName>sample_contacts</RelationshipName>
                  </parameters>
                </control>
              </cell></row>
            </rows>
          </section></sections></column></columns>
        </tab></tabs></form>
        """;

    private static EntityMetadata SampleEntity() =>
        MetadataFactory.Entity("sample", "Sample")
            .PrimaryId("sampleid", "SampleId").Display("Sample")
            .String("name", "Name", maxLength: 100).Display("Name")
            .Memo("description", "Description", maxLength: 2000, format: StringFormat.TextArea).Display("Description")
            .Integer("count", "Count", min: 0, max: 10).Display("Count")
            .BigInt("bignum", "BigNum", min: 0).Display("Big Num")
            .Decimal("ratio", "Ratio", min: 0m, max: 1m, precision: 2).Display("Ratio")
            .Double("score", "Score", precision: 2).Display("Score")
            .Money("revenue", "Revenue", min: 0m, precision: 2).Display("Revenue")
            .Boolean("flag", "Flag").Display("Flag")
            .DateTime("when", "When", DateTimeFormat.DateAndTime).Display("When")
            .Choice("color", "Color", global: false, null, (1, "Red"), (2, "Green")).Display("Color")
            .MultiSelect("tags", "Tags", global: false, null, (1, "A"), (2, "B")).Display("Tags")
            .Lookup("ownerid", "OwnerId", "systemuser", "team").Display("Owner")
            .PartyList("attendees", "Attendees", "systemuser").Display("Attendees")
            .Image("photo", "Photo").Display("Photo")
            .File("contract", "Contract").Display("Contract")
            .Build();

    [OptInFact("VELA_TS_SMOKE=1")] // the assertions below need a TypeScript toolchain
    public void Generated_Ts_Compiles_Against_FormModel()
    {
        string? clientDir = Environment.GetEnvironmentVariable("VELA_CLIENT_DIR");
        string? modelPath = Environment.GetEnvironmentVariable("VELA_CLIENT_MODEL")
            ?? (clientDir != null ? Path.Combine(clientDir, "src", "form", "model.ts") : null);
        Assert.True(modelPath != null && File.Exists(modelPath),
            $"VELA_TS_SMOKE=1 requires VELA_CLIENT_DIR (vela-client package root) or VELA_CLIENT_MODEL. Resolved model.ts: {modelPath ?? "<null>"}");

        string? tscEntry = ResolveTscEntry(clientDir);
        Assert.True(tscEntry != null,
            "Could not find TypeScript's tsc. Run 'npm install' in VELA_CLIENT_DIR, or set VELA_TSC to <root>/node_modules/typescript/bin/tsc.");

        string work = Path.Combine(Path.GetTempPath(), "vela-ts-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            foreach (GeneratedModel model in FormModelTsGenerator.Generate(new[]
            {
                new FormSource("7c1e3f2a-0000-0000-0000-0000000000aa", "Main", "All", FormXmlParser.Parse(SampleFormXml), SampleEntity()),
            }))
            {
                File.WriteAllText(Path.Combine(work, model.FileName), model.Code);
            }

            // Shim the bare `vela-client` import to the real model.ts via a tsconfig path mapping.
            File.Copy(modelPath!, Path.Combine(work, "vela-client.ts"));
            File.WriteAllText(Path.Combine(work, "tsconfig.json"), """
                {
                  "compilerOptions": {
                    "strict": true,
                    "noEmit": true,
                    "target": "ES2020",
                    "module": "ESNext",
                    "moduleResolution": "Bundler",
                    "skipLibCheck": true,
                    "paths": { "vela-client": ["./vela-client.ts"] }
                  },
                  "include": ["*.ts"]
                }
                """);

            (int exitCode, string output) = RunTsc(tscEntry!, work);
            Assert.True(exitCode == 0, $"tsc reported type errors:\n{output}");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { /* best-effort cleanup */ }
        }
    }

    private static string? ResolveTscEntry(string? clientDir)
    {
        string? explicitTsc = Environment.GetEnvironmentVariable("VELA_TSC");
        if (!string.IsNullOrWhiteSpace(explicitTsc) && File.Exists(explicitTsc))
        {
            return explicitTsc;
        }

        if (!string.IsNullOrWhiteSpace(clientDir))
        {
            string candidate = Path.Combine(clientDir, "node_modules", "typescript", "bin", "tsc");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static (int ExitCode, string Output) RunTsc(string tscEntry, string workingDirectory)
    {
        // tsc's entry is a Node script; run it through `node` so it works cross-platform without a shell.
        ProcessStartInfo info = new ProcessStartInfo("node")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(tscEntry);
        info.ArgumentList.Add("--noEmit");
        info.ArgumentList.Add("-p");
        info.ArgumentList.Add("tsconfig.json");

        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException("Failed to start 'node' for tsc. Is Node.js on PATH?");
        StringBuilder output = new StringBuilder();
        output.Append(process.StandardOutput.ReadToEnd());
        output.Append(process.StandardError.ReadToEnd());
        process.WaitForExit();
        return (process.ExitCode, output.ToString());
    }
}
