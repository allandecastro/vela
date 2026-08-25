using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>Unit tests for the pure <see cref="FormScriptScaffold"/> helpers used by <c>vela add form-script</c>.</summary>
public class FormScriptScaffoldTests
{
    [Theory]
    [InlineData("account", true)]
    [InlineData("new_order", true)]
    [InlineData("Account", false)]   // logical names are lower-case
    [InlineData("9bad", false)]
    [InlineData("", false)]
    public void IsValidEntity_Validates_LogicalNames(string entity, bool expected) =>
        Assert.Equal(expected, FormScriptScaffold.IsValidEntity(entity));

    [Fact]
    public void EntryName_And_SourcePath_Mirror_Each_Other()
    {
        Assert.Equal("adc_/js/account/Forms/main", FormScriptScaffold.EntryName("adc", "account"));
        Assert.Equal("src/account/Forms/main.ts", FormScriptScaffold.SourcePath("account"));
    }

    [Theory]
    [InlineData("account", "Account")]
    [InlineData("new_order", "NewOrder")]
    public void Pascal_Splits_On_Underscore(string entity, string expected) =>
        Assert.Equal(expected, FormScriptScaffold.Pascal(entity));

    [Fact]
    public void WebResourcePrefixFromTsup_Reads_First_Segment_Of_GlobalName()
    {
        string tsup = ScaffoldTemplates.WebResourcesTsupConfig("adc.sales", "dist");
        Assert.Equal("adc", FormScriptScaffold.WebResourcePrefixFromTsup(tsup));
        Assert.Null(FormScriptScaffold.WebResourcePrefixFromTsup("no global name here"));
    }

    [Fact]
    public void FormScript_Exports_Both_Handlers_And_Shows_defineForm()
    {
        string script = FormScriptScaffold.FormScript("new_order");
        Assert.Contains("export function onLoad(", script);
        Assert.Contains("export function onSave(", script);
        Assert.Contains("defineForm(NewOrderMain", script);
        Assert.Contains("../../generated/new_order.main.form", script);
    }

    [Fact]
    public void TryInsertEntry_Inserts_A_New_Entry_Before_The_Closing_Brace()
    {
        string entries = ScaffoldTemplates.WebResourcesEntries("adc.sales");

        bool ok = FormScriptScaffold.TryInsertEntry(
            entries, "adc_/js/contact/Forms/main", "src/contact/Forms/main.ts", out string updated, out bool inserted);

        Assert.True(ok);
        Assert.True(inserted);
        Assert.Contains("'adc_/js/contact/Forms/main': 'src/contact/Forms/main.ts',", updated);
        // The new line sits inside the object, before the closing `} as const;`.
        Assert.True(updated.IndexOf("adc_/js/contact", System.StringComparison.Ordinal)
            < updated.IndexOf("} as const", System.StringComparison.Ordinal));
    }

    [Fact]
    public void TryInsertEntry_Is_Idempotent_For_An_Existing_Entry()
    {
        // The scaffolded map already contains the account entry.
        string entries = ScaffoldTemplates.WebResourcesEntries("adc.sales");

        bool ok = FormScriptScaffold.TryInsertEntry(
            entries, "adc_/js/account/Forms/main", "src/account/Forms/main.ts", out string updated, out bool inserted);

        Assert.True(ok);
        Assert.False(inserted);
        Assert.Equal(entries, updated);
    }

    [Fact]
    public void TryInsertEntry_Fails_When_The_Map_Is_Not_Found()
    {
        bool ok = FormScriptScaffold.TryInsertEntry(
            "export const notEntries = 1;", "adc_/js/x/Forms/main", "src/x/Forms/main.ts", out _, out bool inserted);

        Assert.False(ok);
        Assert.False(inserted);
    }
}
