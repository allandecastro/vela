using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.TypedImageProjectionAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class TypedImageProjectionAnalyzerTests
{
    // Self-contained stand-in: the config surface (with WithPreImage/WithPostImage), the typed-image terminals on
    // a type named TypedImageHandlers (what the analyzer keys on), an Entity + AttributeLogicalName, and two
    // projection shapes. The analyzer resolves everything from this source.
    private const string Harness = @"
using System;
using System.Linq.Expressions;

namespace Microsoft.Xrm.Sdk
{
    public class Entity { }
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class AttributeLogicalNameAttribute : Attribute { public AttributeLogicalNameAttribute(string logicalName) { } }
}

namespace Vela.Steps
{
    using System;
    using System.Linq.Expressions;
    using Microsoft.Xrm.Sdk;

    public interface IFluentStep { }
    public interface IUpdatePostContext<T> { }

    public interface IUpdatePostOperationConfig<T> : IFluentStep where T : Entity
    {
        IUpdatePostOperationConfig<T> FilterOn(Expression<Func<T, object>> a);
        IUpdatePostOperationConfig<T> WithPreImage(params Expression<Func<T, object>>[] a);
        IUpdatePostOperationConfig<T> WithPostImage(params Expression<Func<T, object>>[] a);
    }

    public static class TypedImageHandlers
    {
        public static void Handle<TEntity, TPre, TPost>(this IUpdatePostOperationConfig<TEntity> c, Action<IUpdatePostContext<TEntity>, TPre, TPost> h)
            where TEntity : Entity where TPre : class, new() where TPost : class, new() { }

        public static void HandleWithPostImage<TEntity, TPost>(this IUpdatePostOperationConfig<TEntity> c, Action<IUpdatePostContext<TEntity>, TPost> h)
            where TEntity : Entity where TPost : class, new() { }
    }
}

namespace Demo
{
    using Vela.Steps;
    using Microsoft.Xrm.Sdk;

    public class Account : Entity
    {
        [AttributeLogicalName(""name"")] public string Name { get; set; }
        [AttributeLogicalName(""telephone1"")] public string Telephone1 { get; set; }
    }

    public sealed class NameOnly
    {
        [AttributeLogicalName(""name"")] public string Name { get; set; }
    }

    public sealed class NameAndPhone
    {
        [AttributeLogicalName(""name"")] public string Name { get; set; }
        [AttributeLogicalName(""telephone1"")] public string Telephone1 { get; set; }
    }

    public static class Chain
    {
        public static IUpdatePostOperationConfig<Account> Step() => null;
    }
}
";

    private static string InMethod(string body) => Harness + @"
namespace Demo
{
    using System;
    using Vela.Steps;
    public static class Use
    {
        public static void M()
        {
            " + body + @"
        }
    }
}
";

    [Fact]
    public async Task Flags_a_projection_column_the_step_does_not_capture()
    {
        // Post-image captures only name; NameAndPhone reads telephone1 too -> flagged.
        string code = InMethod(
            "Chain.Step().WithPreImage(a => a.Name).WithPostImage(a => a.Name)" +
            ".{|#0:Handle|}((IUpdatePostContext<Account> ctx, NameOnly pre, NameAndPhone post) => { });");

        await Verify.VerifyAnalyzerAsync(
            code,
            new DiagnosticResult(Vela.Analyzers.TypedImageProjectionAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
                .WithLocation(0)
                .WithArguments("NameAndPhone", "telephone1"));
    }

    [Fact]
    public async Task Does_not_flag_when_every_projection_column_is_captured()
    {
        string code = InMethod(
            "Chain.Step().WithPreImage(a => a.Name).WithPostImage(a => a.Name, a => a.Telephone1)" +
            ".Handle((IUpdatePostContext<Account> ctx, NameOnly pre, NameAndPhone post) => { });");

        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_capture_all_image()
    {
        // WithPostImage() with no columns captures everything, so any projection column is fine.
        string code = InMethod(
            "Chain.Step().WithPostImage()" +
            ".HandleWithPostImage((IUpdatePostContext<Account> ctx, NameAndPhone post) => { });");

        await Verify.VerifyAnalyzerAsync(code);
    }
}
