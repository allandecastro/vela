using Vela.Cli;
using Xunit;

namespace Vela.Cli.Tests;

public class ClrTypeNameMapperTests
{
    private const string Asm = ", Microsoft.Xrm.Sdk, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";
    private const string Cor = ", mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";

    [Theory]
    [InlineData("Microsoft.Xrm.Sdk.Entity", "Entity")]
    [InlineData("Microsoft.Xrm.Sdk.EntityReference", "EntityReference")]
    [InlineData("Microsoft.Xrm.Sdk.EntityCollection", "EntityCollection")]
    [InlineData("Microsoft.Xrm.Sdk.OptionSetValue", "OptionSetValue")]
    [InlineData("Microsoft.Xrm.Sdk.Money", "Money")]
    [InlineData("Microsoft.Xrm.Sdk.Relationship", "Relationship")]
    [InlineData("Microsoft.Xrm.Sdk.EntityReferenceCollection", "EntityReferenceCollection")]
    [InlineData("Microsoft.Xrm.Sdk.Query.ColumnSet", "ColumnSet")]
    [InlineData("Microsoft.Xrm.Sdk.Query.QueryBase", "QueryBase")]
    public void Maps_SdkTypes(string fullName, string expected)
    {
        Assert.Equal(expected, ClrTypeNameMapper.Map(fullName + Asm));
    }

    [Theory]
    [InlineData("System.Guid", "Guid")]
    [InlineData("System.Boolean", "bool")]
    [InlineData("System.Int32", "int")]
    [InlineData("System.Int64", "long")]
    [InlineData("System.Double", "double")]
    [InlineData("System.Decimal", "decimal")]
    [InlineData("System.String", "string")]
    [InlineData("System.DateTime", "DateTime")]
    public void Maps_Primitives(string fullName, string expected)
    {
        Assert.Equal(expected, ClrTypeNameMapper.Map(fullName + Cor));
    }

    [Fact]
    public void Maps_StringArray()
    {
        Assert.Equal("string[]", ClrTypeNameMapper.Map("System.String[]" + Cor));
    }

    [Fact]
    public void Maps_ByteArray()
    {
        Assert.Equal("byte[]", ClrTypeNameMapper.Map("System.Byte[]" + Cor));
    }

    [Fact]
    public void Maps_Nullable_To_QuestionMark()
    {
        string clr = $"System.Nullable`1[[System.Int32{Cor}]]{Cor}";
        Assert.Equal("int?", ClrTypeNameMapper.Map(clr));
    }

    [Fact]
    public void Maps_Nullable_Of_DateTime()
    {
        string clr = $"System.Nullable`1[[System.DateTime{Cor}]]{Cor}";
        Assert.Equal("DateTime?", ClrTypeNameMapper.Map(clr));
    }

    [Fact]
    public void Maps_GenericList_To_FullyQualified_Generic()
    {
        string clr = $"System.Collections.Generic.List`1[[Microsoft.Xrm.Sdk.Entity{Asm}]]{Cor}";
        Assert.Equal("System.Collections.Generic.List<Entity>", ClrTypeNameMapper.Map(clr));
    }

    [Fact]
    public void Maps_GenericIEnumerable()
    {
        string clr = $"System.Collections.Generic.IEnumerable`1[[System.String{Cor}]]{Cor}";
        Assert.Equal("System.Collections.Generic.IEnumerable<string>", ClrTypeNameMapper.Map(clr));
    }

    [Fact]
    public void Maps_GenericDictionary()
    {
        string clr = $"System.Collections.Generic.Dictionary`2[[System.String{Cor}],[System.Int32{Cor}]]{Cor}";
        Assert.Equal("System.Collections.Generic.Dictionary<string, int>", ClrTypeNameMapper.Map(clr));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_Maps_To_Object(string? input)
    {
        Assert.Equal("object", ClrTypeNameMapper.Map(input));
    }

    [Fact]
    public void Unknown_Type_Maps_To_Object()
    {
        Assert.Equal("object", ClrTypeNameMapper.Map("Some.Unknown.Type" + Asm));
    }

    [Fact]
    public void Unknown_GenericCollection_Maps_To_Object()
    {
        string clr = $"System.Collections.Concurrent.ConcurrentBag`1[[System.String{Cor}]]{Cor}";
        Assert.Equal("object", ClrTypeNameMapper.Map(clr));
    }

    [Fact]
    public void Malformed_Input_Maps_To_Object()
    {
        Assert.Equal("object", ClrTypeNameMapper.Map("System.Nullable`1[[[[[broken"));
    }

    [Fact]
    public void BareTypeName_WithoutAssembly_StillMaps()
    {
        Assert.Equal("EntityReference", ClrTypeNameMapper.Map("Microsoft.Xrm.Sdk.EntityReference"));
    }
}
