using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.HttpClientTimeoutAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class HttpClientTimeoutAnalyzerTests
{
    private const string Harness = @"
namespace Microsoft.Xrm.Sdk { public interface IPlugin { } }
namespace Demo
{
    public sealed class Services
    {
        public Services AddScoped<T>(System.Func<Services, T> factory) => this;
    }
    public static class Json
    {
        public static void Use(this System.Net.Http.HttpClient client) { }
    }
}
";

    private static DiagnosticResult Expected(int location = 0) =>
        new DiagnosticResult(Vela.Analyzers.HttpClientTimeoutAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithLocation(location);

    private static string Plugin(string body, string members = "") => Harness + @"
namespace Demo
{
    using System;
    using System.Net.Http;
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin
    {
        " + members + @"
        public void Execute(IServiceProvider sp)
        {
            " + body + @"
        }
    }
}";

    [Fact]
    public async Task Flags_a_local_client_that_never_sets_Timeout()
    {
        string code = Plugin(@"
            using (var client = {|#0:new HttpClient()|})
            {
                client.GetStringAsync(""https://api.contoso.com"");
                client.Use();
            }");
        await Verify.VerifyAnalyzerAsync(code, Expected());
    }

    [Fact]
    public async Task Flags_a_client_used_in_place_and_one_in_a_target_typed_new()
    {
        string code = Plugin(@"
            {|#0:new HttpClient()|}.GetStringAsync(""https://api.contoso.com"");
            HttpClient other = {|#1:new()|};");
        await Verify.VerifyAnalyzerAsync(code, Expected(0), Expected(1));
    }

    [Fact]
    public async Task Does_not_flag_Timeout_set_in_the_initializer_or_later_on_the_local()
    {
        string code = Plugin(@"
            var a = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var b = new HttpClient(new HttpClientHandler(), true);
            b.Timeout = TimeSpan.FromSeconds(15);
            HttpClient c;
            c = new HttpClient();
            Action later = () => c.Timeout = TimeSpan.FromSeconds(15);");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Flags_a_field_whose_Timeout_is_set_in_another_method()
    {
        string code = Plugin(@"_client = {|#0:new HttpClient()|};",
            @"private HttpClient _client;
        private static readonly HttpClient Shared = {|#1:new HttpClient()|};
        private static readonly HttpClient Configured = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private void Configure() { _client.Timeout = TimeSpan.FromSeconds(10); Shared.Timeout = TimeSpan.FromSeconds(10); }");
        await Verify.VerifyAnalyzerAsync(code, Expected(0), Expected(1));
    }

    [Fact]
    public async Task Does_not_flag_a_field_whose_Timeout_is_set_in_the_same_method()
    {
        string code = Plugin(@"
            Client = new HttpClient();
            Client.Timeout = TimeSpan.FromSeconds(10);",
            @"private HttpClient Client { get; set; }");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_follow_a_client_returned_or_passed_out()
    {
        string code = Plugin(@"
            Keep(new HttpClient());
            var a = new HttpClient();
            Keep(a);
            var b = new HttpClient();
            var copy = b;
            HttpClient Local() => new HttpClient();",
            @"private static void Keep(HttpClient client) { }
        private static HttpClient Create() => new HttpClient();
        private static HttpClient CreateLocal() { var c = new HttpClient(); return c; }");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Flags_a_factory_lambda_without_Timeout_in_ConfigureServices()
    {
        string code = Plugin(@"",
            @"private void ConfigureServices(Services services)
        {
            services.AddScoped(_ => {|#0:new HttpClient()|});
            services.AddScoped(_ => { var c = {|#1:new HttpClient()|}; return c; });
            services.AddScoped(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(20) });
            services.AddScoped(_ => { var c = new HttpClient(); c.Timeout = TimeSpan.FromSeconds(20); return c; });
        }");
        await Verify.VerifyAnalyzerAsync(code, Expected(0), Expected(1));
    }

    [Fact]
    public async Task Does_not_flag_a_client_outside_a_plugin_type()
    {
        string code = Harness + @"
namespace Demo
{
    public static class Helper
    {
        public static string Get() { var c = new System.Net.Http.HttpClient(); return c.GetStringAsync(""https://x.contoso.com"").Result; }
    }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }
}
