using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk.Metadata;
using Vela.Cli;
using Vela.Cli.Tests.TestSupport;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>
/// The combined <c>vela models --forms --tables</c> path: form models and table types share one option-sets
/// registry (so an entity's enum file is emitted once, as the union) and one root barrel re-exports both. This
/// mirrors what <c>ModelsCommand</c> orchestrates, exercised at the generator level.
/// </summary>
public class CombinedFormsTablesTests
{
    private const string AccountFormXml = """
        <form><tabs><tab name="general">
          <labels><label description="General" languagecode="1033" /></labels>
          <columns><column width="100%"><sections><section name="s">
            <labels><label description="Sec" languagecode="1033" /></labels>
            <rows>
              <row><cell><control datafieldname="name" /></cell></row>
              <row><cell><control datafieldname="statuscode" /></cell></row>
            </rows>
          </section></sections></column></columns>
        </tab></tabs></form>
        """;

    private static EntityMetadata Account() =>
        MetadataFactory.Entity("account", "Account")
            .PrimaryId("accountid", "AccountId").Display("Account")
            .String("name", "Name").Display("Account Name")
            .Choice("statuscode", "StatusCode", global: false, null, (1, "Active"), (2, "Inactive")).Display("Status")
            .Build();

    private static IReadOnlyList<GeneratedModel> GenerateCombined()
    {
        EntityMetadata account = Account();
        var form = new FormSource("id", "Main", "Main", FormXmlParser.Parse(AccountFormXml), account);

        OptionSetsTs.Registry optionSets = new OptionSetsTs.Registry();
        List<GeneratedModel> files = new List<GeneratedModel>();
        files.AddRange(FormModelTsGenerator.GenerateForms(new[] { form }, optionSets));
        files.AddRange(TableTypeTsGenerator.GenerateTables(new[] { account }, optionSets));
        files.AddRange(OptionSetsTs.GenerateFiles(optionSets));
        GeneratedModel? barrel = BarrelTs.Build(files);
        if (barrel != null)
        {
            files.Add(barrel);
        }

        return files;
    }

    [Fact]
    public void Option_Set_File_Is_Shared_Once_Across_Both_Scopes()
    {
        var files = GenerateCombined();

        // The form and the table both reference account_statuscode, but the shared registry emits one enum file.
        Assert.Single(files, f => f.FileName == "option-sets/account.ts");
        string optionSets = files.Single(f => f.FileName == "option-sets/account.ts").Code;
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(optionSets, "export enum account_statuscode").Count);
    }

    [Fact]
    public void One_Barrel_Re_Exports_Both_The_Form_Const_And_The_Table()
    {
        var files = GenerateCombined();

        Assert.Single(files, f => f.FileName == "index.ts");
        string barrel = files.Single(f => f.FileName == "index.ts").Code;

        Assert.Contains("export { AccountMain } from './forms/account/main.form';", barrel);
        Assert.Contains("export { Account } from './tables/account';", barrel);
        Assert.Contains("export { account_statuscode } from './option-sets/account';", barrel);
    }

    [Fact]
    public void Emits_The_Form_And_The_Table_Files()
    {
        string[] names = GenerateCombined().Select(f => f.FileName).ToArray();

        Assert.Contains("forms/account/main.form.ts", names);
        Assert.Contains("tables/account.ts", names);
    }
}
