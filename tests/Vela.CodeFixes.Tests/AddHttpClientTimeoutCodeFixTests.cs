using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.CodeFixVerifier<
    Vela.Analyzers.HttpClientTimeoutAnalyzer,
    Vela.CodeFixes.AddHttpClientTimeoutCodeFix>;

namespace Vela.CodeFixes.Tests;

public sealed class AddHttpClientTimeoutCodeFixTests
{
    private const string Harness = @"
namespace Microsoft.Xrm.Sdk { public interface IPlugin { } }
namespace Demo
{
    using System;
    using System.Net.Http;
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin { public void Execute(IServiceProvider sp) { BODY } }
}";

    private static DiagnosticResult Expected() =>
        new DiagnosticResult(Vela.Analyzers.HttpClientTimeoutAnalyzer.DiagnosticId, DiagnosticSeverity.Warning).WithLocation(0);

    [Fact]
    public async Task Adds_an_initializer_with_Timeout()
    {
        string before = Harness.Replace("BODY", "var c = {|#0:new HttpClient()|}; c.GetStringAsync(\"https://x.contoso.com\");");
        string after = Harness.Replace("BODY", "var c = new HttpClient() { Timeout = TimeSpan.FromSeconds(30) }; c.GetStringAsync(\"https://x.contoso.com\");");
        await Verify.VerifyCodeFixAsync(before, Expected(), after);
    }

    [Fact]
    public async Task Appends_Timeout_to_an_existing_initializer()
    {
        string before = Harness.Replace("BODY", "var c = {|#0:new HttpClient { MaxResponseContentBufferSize = 1024 }|}; c.GetStringAsync(\"https://x.contoso.com\");");
        string after = Harness.Replace("BODY", "var c = new HttpClient { MaxResponseContentBufferSize = 1024, Timeout = TimeSpan.FromSeconds(30) }; c.GetStringAsync(\"https://x.contoso.com\");");
        await Verify.VerifyCodeFixAsync(before, Expected(), after);
    }

    [Fact]
    public async Task Fixes_a_target_typed_new()
    {
        string before = Harness.Replace("BODY", "HttpClient c = {|#0:new()|}; c.GetStringAsync(\"https://x.contoso.com\");");
        string after = Harness.Replace("BODY", "HttpClient c = new() { Timeout = TimeSpan.FromSeconds(30) }; c.GetStringAsync(\"https://x.contoso.com\");");
        await Verify.VerifyCodeFixAsync(before, Expected(), after);
    }

    [Fact]
    public async Task Qualifies_TimeSpan_without_using_System_and_fills_an_empty_initializer()
    {
        const string Bare = @"
namespace Microsoft.Xrm.Sdk { public interface IPlugin { } }
namespace Demo
{
    public class MyPlugin : Microsoft.Xrm.Sdk.IPlugin { public void Execute(System.IServiceProvider sp) { BODY } }
}";
        string before = Bare.Replace("BODY", "var c = {|#0:new System.Net.Http.HttpClient { }|}; c.GetStringAsync(\"https://x.contoso.com\");");
        string after = Bare.Replace("BODY", "var c = new System.Net.Http.HttpClient { Timeout = System.TimeSpan.FromSeconds(30) }; c.GetStringAsync(\"https://x.contoso.com\");");
        await Verify.VerifyCodeFixAsync(before, Expected(), after);
    }
}
