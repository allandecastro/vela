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
    public async Task Flags_a_local_initialized_with_an_IP_uri_and_never_written_again()
    {
        string code = Plugin(@"
            string url = ""http://10.0.0.1/api"";
            string other = sp.ToString();
            string later = ""https://10.9.9.9/"";
            other = url;
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            client.GetAsync({|#0:url|});
            client.BaseAddress = new Uri({|#1:url|});
            var req = new HttpRequestMessage(HttpMethod.Post, {|#2:url|});
            var a = new Uri(other);");
        await Verify.VerifyAnalyzerAsync(code,
            Expected("10.0.0.1", 0), Expected("10.0.0.1", 1), Expected("10.0.0.1", 2));
    }

    [Fact]
    public async Task Flags_an_interpolated_prefix_a_concatenation_and_a_chain_held_in_locals()
    {
        string code = Plugin(@"
            int id = 3;
            var items = $""https://10.1.2.3/items/{id}"";
            var a = new Uri({|#0:items|});
            var root = ""https://10.1.2.4/"";
            var b = new Uri({|#1:root + id|});
            var copy = root;
            var path = copy + ""orders"";
            var c = new Uri({|#2:path|});
            object boxed = ""http://10.1.2.5/"";
            var d = new Uri({|#3:(string)boxed|});");
        await Verify.VerifyAnalyzerAsync(code,
            Expected("10.1.2.3", 0), Expected("10.1.2.4", 1), Expected("10.1.2.4", 2), Expected("10.1.2.5", 3));
    }

    [Fact]
    public async Task Does_not_flag_a_local_written_after_its_declaration()
    {
        string code = Plugin(@"
            string reassigned = ""https://10.0.0.1/"";
            reassigned = sp.ToString();
            var a = new Uri(reassigned);
            string branch = ""https://10.0.0.2/"";
            if (sp == null) { branch = ""https://api.contoso.com/""; }
            var b = new Uri(branch);
            string compound = ""https://10.0.0.3"";
            compound += "".contoso.com/"";
            var c = new Uri(compound);
            string coalesce = ""https://10.0.0.4/"";
            coalesce ??= ""x"";
            var d = new Uri(coalesce);
            string byRef = ""https://10.0.0.5/"";
            Swap(ref byRef);
            var e = new Uri(byRef);
            string byOut = ""https://10.0.0.6/"";
            Fill(out byOut);
            var f = new Uri(byOut);
            string tuple = ""https://10.0.0.7/"";
            int n;
            (tuple, n) = (sp.ToString(), 1);
            var g = new Uri(tuple);
            string aliased = ""https://10.0.0.8/"";
            ref string alias = ref aliased;
            alias = sp.ToString();
            var h = new Uri(aliased);
            string captured = ""https://10.0.0.9/"";
            Action reset = () => captured = sp.ToString();
            var i = new Uri(captured);
            Address address = ""https://10.0.0.10/"";
            address++;
            var j = new Uri(address);
            string declared;
            declared = ""https://10.0.0.11/"";
            var k = new Uri(declared);",
            @"private static void Swap(ref string s) { s = ""https://api.contoso.com/""; }
        private static void Fill(out string s) { s = ""https://api.contoso.com/""; }") + @"
namespace Demo
{
    public struct Address
    {
        private readonly string _value;
        private Address(string value) { _value = value; }
        public static implicit operator Address(string value) => new Address(value);
        public static implicit operator string(Address address) => address._value;
        public static Address operator ++(Address address) => new Address(""https://api.contoso.com/"");
    }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_follow_a_field_a_parameter_a_named_host_or_a_self_referencing_local()
    {
        string code = Plugin(@"
            var a = new Uri(_endpoint);
            Call(""https://10.0.0.2/"");
            string named = ""https://api.contoso.com/"";
            var b = new Uri(named);
            string self = {|#0:self|};
            var c = new Uri(self);",
            @"private readonly string _endpoint = ""https://10.0.0.1/"";
        private static Uri Call(string url) => new Uri(url);");
        await Verify.VerifyAnalyzerAsync(code, DiagnosticResult.CompilerError("CS0165").WithLocation(0).WithArguments("self"));
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

    [Fact]
    public async Task Flags_a_service_of_a_plugin_assembly()
    {
        // The service runs in the sandbox with the plugin that resolves it, so the whole plugin assembly is checked.
        string code = Plugin("") + @"
namespace Demo
{
    public sealed class ErpClient
    {
        public System.Uri Endpoint() => new System.Uri({|#0:""https://10.0.0.9/erp""|});
    }
}";
        await Verify.VerifyAnalyzerAsync(code, Expected("10.0.0.9"));
    }

    [Fact]
    public async Task Does_not_flag_a_helper_of_a_test_project_that_declares_a_plugin()
    {
        // A test project (it references Vela.Testing) may declare a fake plugin; its helpers are not sandbox code.
        var test = new Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerTest<Vela.Analyzers.IpAddressCallAnalyzer, Microsoft.CodeAnalysis.Testing.DefaultVerifier>
        {
            TestCode = Plugin("") + @"
namespace Demo
{
    public static class Helper
    {
        public static System.Uri Local() => new System.Uri(""http://127.0.0.1/"");
    }
}",
        };
        test.TestState.AdditionalReferences.Add(AnalyzerTestReferences.VelaTesting);
        await test.RunAsync();
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
