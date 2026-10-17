using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.IpAddressCallAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class IpAddressCallAnalyzerTests
{
    private const string Harness = @"
namespace Microsoft.Xrm.Sdk { public interface IPlugin { } }
";

    private static DiagnosticResult Expected(string host, int location = 0) =>
        new DiagnosticResult(Vela.Analyzers.IpAddressCallAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithLocation(location)
            .WithArguments(host);

    private static string Plugin(string body, string members = "") => Harness + @"
namespace Demo
{
    using System;
    using System.Net;
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
    public async Task Flags_an_IPv4_uri_literal()
    {
        string code = Plugin(@"var u = new Uri({|#0:""https://10.0.0.1/api""|});");
        await Verify.VerifyAnalyzerAsync(code, Expected("10.0.0.1"));
    }

    [Fact]
    public async Task Flags_an_IPv6_host_in_brackets()
    {
        string code = Plugin(@"var u = new Uri({|#0:""http://[2001:db8::1]:8080/x""|}, UriKind.Absolute);");
        await Verify.VerifyAnalyzerAsync(code, Expected("[2001:db8::1]"));
    }

    [Fact]
    public async Task Flags_HttpClient_calls_HttpRequestMessage_BaseAddress_and_WebRequest()
    {
        string code = Plugin(@"
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            client.BaseAddress = new Uri({|#0:""https://192.168.1.10""|});
            client.GetStringAsync({|#1:""http://192.168.1.11/x""|});
            var req = new HttpRequestMessage(HttpMethod.Get, {|#2:""https://192.168.1.12/y""|});
            var web = WebRequest.Create({|#3:""http://192.168.1.13/z""|});");
        await Verify.VerifyAnalyzerAsync(code,
            Expected("192.168.1.10", 0), Expected("192.168.1.11", 1), Expected("192.168.1.12", 2), Expected("192.168.1.13", 3));
    }

    [Fact]
    public async Task Flags_a_constant_an_interpolated_prefix_and_a_concatenation()
    {
        string code = Plugin(@"
            var a = new Uri({|#0:Endpoint|});
            int id = 3;
            var b = new Uri({|#1:$""https://10.1.2.3/items/{id}""|});
            var c = new Uri({|#2:$""http://10.1.2.4:{id}/""|});
            var d = new Uri({|#3:""https://10.1.2.5/"" + id|});",
            @"private const string Endpoint = ""https://172.16.0.1/svc"";");
        await Verify.VerifyAnalyzerAsync(code,
            Expected("172.16.0.1", 0), Expected("10.1.2.3", 1), Expected("10.1.2.4", 2), Expected("10.1.2.5", 3));
    }

    [Fact]
    public async Task Does_not_flag_named_hosts_relative_or_non_http_strings()
    {
        string code = Plugin(@"
            var a = new Uri(""https://api.contoso.com/v1"");
            var b = new Uri(""http://localhost:5000/"");
            var c = new Uri(""ftp://10.0.0.1/file"");
            var d = new Uri(""/relative/10.0.0.1"", UriKind.Relative);
            var e = new Uri(""not a uri"", UriKind.RelativeOrAbsolute);
            string label = ""https://10.0.0.1/"";");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_uri_built_from_a_variable_or_a_host_cut_by_an_interpolation()
    {
        string code = Plugin(@"
            string url = sp.ToString();
            var a = new Uri(url);
            int n = 1;
            var b = new Uri($""https://10.0.0.{n}/"");
            var c = new Uri($""{url}/10.0.0.1"");
            var d = new Uri(url + ""https://10.0.0.1/"");
            var e = new Uri($""https://[::{n}]"");");
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_an_IP_uri_outside_a_plugin_type()
    {
        string code = Harness + @"
namespace Demo
{
    public static class Helper
    {
        public static System.Uri Local() => new System.Uri(""http://127.0.0.1/"");
    }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Theory]
    [InlineData("https://10.0.0.1/", "10.0.0.1")]
    [InlineData("https://10.0.0.1?q", "10.0.0.1")]
    [InlineData("https://10.0.0.1:", "10.0.0.1")]
    [InlineData("https://[::1]:", "[::1]")]
    [InlineData("https://[::1]/", "[::1]")]
    [InlineData("https://10.0.0.", null)]
    [InlineData("https://[::1]", null)]
    [InlineData("https://api.contoso.com/", null)]
    [InlineData("10.0.0.1/", null)]
    public void Prefix_counts_only_when_it_holds_the_whole_host(string prefix, string? host)
        => Assert.Equal(host, Vela.Analyzers.IpAddressCallAnalyzer.IpHostOfPrefix(prefix));
}
