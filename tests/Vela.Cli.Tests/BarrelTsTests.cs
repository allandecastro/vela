using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

/// <summary>Unit tests for <see cref="BarrelTs"/>, the root index.ts barrel builder.</summary>
public class BarrelTsTests
{
    [Fact]
    public void Re_Exports_All_Named_Export_Forms()
    {
        // Not just const/interface/enum: a future generated file could use type/function/class/let, and the barrel
        // must still re-export them.
        var files = new[]
        {
            new GeneratedModel("a.ts", "export const A = 1;\nexport type B = string;\nexport function c() {}\n"),
            new GeneratedModel("b.ts", "export abstract class D {}\nexport enum E { X }\nexport async function f() {}\nexport let g = 1;\n"),
        };

        GeneratedModel? barrel = BarrelTs.Build(files);

        Assert.NotNull(barrel);
        Assert.Equal("index.ts", barrel!.FileName);
        // Values via `export { }`, the type-only alias via `export type { }` (so it survives isolated-modules).
        Assert.Contains("export { A, c } from './a';", barrel.Code);
        Assert.Contains("export type { B } from './a';", barrel.Code);
        Assert.Contains("export { D, E, f, g } from './b';", barrel.Code);
    }

    [Fact]
    public void Module_specifier_uses_forward_slashes_for_a_nested_windows_path()   // B11
    {
        var files = new[]
        {
            new GeneratedModel("forms\\account\\main.form.ts", "export const A = 1;\n"),
        };

        GeneratedModel? barrel = BarrelTs.Build(files);

        Assert.NotNull(barrel);
        Assert.Contains("from './forms/account/main.form';", barrel!.Code);   // forward slashes, .ts dropped
        Assert.DoesNotContain("\\", barrel.Code);
    }

    [Fact]
    public void Dedups_A_Name_To_The_First_Occurrence()
    {
        var files = new[]
        {
            new GeneratedModel("first.ts", "export const Shared = 1;\n"),
            new GeneratedModel("second.ts", "export interface Shared {}\nexport const Other = 2;\n"),
        };

        GeneratedModel? barrel = BarrelTs.Build(files);

        Assert.NotNull(barrel);
        Assert.Contains("export { Shared } from './first';", barrel!.Code);
        // The later Shared is dropped; only the unique name survives on the second module.
        Assert.Contains("export { Other } from './second';", barrel.Code);
    }

    [Fact]
    public void Returns_Null_When_There_Is_Nothing_To_Export()
    {
        Assert.Null(BarrelTs.Build(new[] { new GeneratedModel("x.ts", "// no exports here\nconst local = 1;\n") }));
    }
}
