using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.StaticHandlerAnalyzer>;

namespace Vela.Analyzers.Tests;

// VELA004 edges: a Handle() without an argument, and static lambdas (a lambda is never a static method handler,
// even when declared static).
public sealed class StaticHandlerAnalyzerEdgeTests
{
    private const string Harness = @"
namespace Vela.Steps
{
    public interface IFluentStep { }
    public interface IStep : IFluentStep
    {
        void Handle();
        void Handle(System.Action<int> handler);
        void Handle(System.Action<long> handler);
    }
}

namespace Demo
{
    using Vela.Steps;
    public class Use
    {
        protected IStep RegisterStep() => null!;
        private static void Both(int ctx) { }
        private static void Both(long ctx) { }
        private void Mixed(int ctx) { }
        private static void Mixed(long ctx) { }

        public void M()
        {
            BODY
        }
    }
}
";

    private static string In(string body) => Harness.Replace("BODY", body);

    [Fact]
    public async Task A_Handle_call_without_a_handler_is_ignored()
        => await Verify.VerifyAnalyzerAsync(In("RegisterStep().Handle();"));

    [Theory]
    [InlineData("RegisterStep().Handle(static (int x) => { });")]
    [InlineData("RegisterStep().Handle(static (long x) => { });")]
    [InlineData("RegisterStep().Handle(static delegate (int x) { });")]
    [InlineData("System.Action<int> a = static x => { }; RegisterStep().Handle(a);")]
    public async Task A_static_lambda_or_anonymous_method_is_not_a_static_method_handler(string body)
        => await Verify.VerifyAnalyzerAsync(In(body));
}
