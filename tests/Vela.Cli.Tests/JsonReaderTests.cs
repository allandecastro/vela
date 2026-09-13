using System.Text.Json;
using Vela.Cli;
using Xunit;
using static Vela.Cli.Tests.TestSupport.RegistrationFactory;

namespace Vela.Cli.Tests;

// Exercises the edge branches of the JsonReader helpers (absent / wrong-kind properties, fallbacks) that the
// happy-path normalizer/diff tests don't reach — these are what the branch-coverage gate guards.
public class JsonReaderTests
{
    [Fact]
    public void TryGetString_returns_null_when_absent_or_not_a_string()
    {
        JsonElement el = Json(new { s = "x", n = 5 });
        Assert.Equal("x", JsonReader.TryGetString(el, "s"));
        Assert.Null(JsonReader.TryGetString(el, "missing"));
        Assert.Null(JsonReader.TryGetString(el, "n")); // present but a number
    }

    [Fact]
    public void TryGetNestedString_handles_missing_and_non_object()
    {
        Assert.Equal("v", JsonReader.TryGetString(
            Json(new { name = "v" }), "name"));
        Assert.Null(JsonReader.TryGetNestedString(Json(new { obj = new { inner = "v" } }), "missing", "inner")); // object absent
        Assert.Null(JsonReader.TryGetNestedString(Json(new { obj = "notObject" }), "obj", "inner")); // present but not an object
        Assert.Equal("v", JsonReader.TryGetNestedString(Json(new { obj = new { inner = "v" } }), "obj", "inner"));
    }

    [Fact]
    public void TryGetBool_covers_true_false_default_and_non_bool()
    {
        Assert.True(JsonReader.TryGetBool(Json(new { b = true }), "b"));
        Assert.False(JsonReader.TryGetBool(Json(new { b = false }), "b"));
        Assert.True(JsonReader.TryGetBool(Json(new { x = 1 }), "missing", defaultValue: true)); // absent -> default
        Assert.True(JsonReader.TryGetBool(Json(new { b = "yes" }), "b", defaultValue: true)); // present but not a bool -> default
    }

    [Fact]
    public void TryGetInt_covers_number_default_and_non_number()
    {
        Assert.Equal(7, JsonReader.TryGetInt(Json(new { n = 7 }), "n"));
        Assert.Equal(3, JsonReader.TryGetInt(Json(new { x = 1 }), "missing", defaultValue: 3)); // absent -> default
        Assert.Equal(3, JsonReader.TryGetInt(Json(new { n = "9" }), "n", defaultValue: 3)); // present but a string -> default
    }

    [Fact]
    public void TryGetStringArray_orders_filters_blanks_and_ignores_non_strings()
    {
        string[] result = JsonReader.TryGetStringArray(Json(new { a = new object[] { "b", "a", "", 5 } }), "a");
        Assert.Equal(new[] { "a", "b" }, result); // ordered, blank + non-string dropped

        Assert.Empty(JsonReader.TryGetStringArray(Json(new { a = "notArray" }), "a")); // present but not an array
        Assert.Empty(JsonReader.TryGetStringArray(Json(new { x = 1 }), "missing")); // absent
    }

    [Fact]
    public void TryGetArrayLength_counts_or_returns_zero()
    {
        Assert.Equal(2, JsonReader.TryGetArrayLength(Json(new { a = new[] { "x", "y" } }), "a"));
        Assert.Equal(0, JsonReader.TryGetArrayLength(Json(new { a = "notArray" }), "a"));
        Assert.Equal(0, JsonReader.TryGetArrayLength(Json(new { x = 1 }), "missing"));
    }

    [Fact]
    public void TryGetStepImages_reads_pairs_or_returns_empty()
    {
        StepImage[] images = JsonReader.TryGetStepImages(
            Json(new { pre = new object[] { new { alias = "PreA", attributes = new[] { "name" } } } }), "pre");
        StepImage one = Assert.Single(images);
        Assert.Equal("PreA", one.Alias);
        Assert.Equal(new[] { "name" }, one.Attributes);

        Assert.Empty(JsonReader.TryGetStepImages(Json(new { pre = "notArray" }), "pre")); // present but not an array
        Assert.Empty(JsonReader.TryGetStepImages(Json(new { x = 1 }), "missing")); // absent
    }

    [Fact]
    public void CustomApiParameterShapes_fall_back_to_typeName_and_drop_blank_names()
    {
        // parameterTypeName absent -> falls back to the legacy "typeName"; a blank-named param is dropped. The type
        // is canonicalized to the code->name form the live read produces (A4), so "String" -> "System.String".
        object[] inputs = JsonReader.TryGetCustomApiInputShapes(
            Json(new { ps = new object[] { new { name = "A", typeName = "String" }, new { name = "" } } }), "ps");
        JsonElement input = JsonSerializer.SerializeToElement(Assert.Single(inputs));
        Assert.Equal("A", input.GetProperty("name").GetString());
        Assert.Equal("System.String", input.GetProperty("parameterTypeName").GetString());

        object[] outputs = JsonReader.TryGetCustomApiOutputShapes(
            Json(new { ps = new object[] { new { name = "B", typeName = "Integer" } } }), "ps");
        JsonElement output = JsonSerializer.SerializeToElement(Assert.Single(outputs));
        Assert.Equal("System.Int32", output.GetProperty("parameterTypeName").GetString());

        Assert.Empty(JsonReader.TryGetCustomApiInputShapes(Json(new { ps = "notArray" }), "ps"));
    }

    [Theory]
    [InlineData("System.Int64", "System.Int32")]     // A4: equivalent integer types collapse to the stored form
    [InlineData("long", "System.Int32")]
    [InlineData("System.Single", "System.Double")]
    [InlineData("System.Int32", "System.Int32")]     // already canonical -> unchanged (idempotent)
    public void CustomApiParameterType_isCanonicalized_toTheLiveForm(string declared, string expected)
    {
        object[] inputs = JsonReader.TryGetCustomApiInputShapes(
            Json(new { ps = new object[] { new { name = "A", parameterTypeName = declared } } }), "ps");
        JsonElement input = JsonSerializer.SerializeToElement(Assert.Single(inputs));
        Assert.Equal(expected, input.GetProperty("parameterTypeName").GetString());
    }

    [Fact]
    public void SplitCsv_handles_null_empty_and_trims_orders_filters()
    {
        Assert.Empty(JsonReader.SplitCsv(null));
        Assert.Empty(JsonReader.SplitCsv("   "));
        Assert.Equal(new[] { "a", "b", "c" }, JsonReader.SplitCsv(" b , a ,, c "));
    }
}
