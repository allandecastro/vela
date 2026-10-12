using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<Vela.Analyzers.MutablePluginStateAnalyzer>;

namespace Vela.Analyzers.Tests;

public sealed class MutablePluginStateAnalyzerTests
{
    private const string Harness = @"
namespace Microsoft.Xrm.Sdk { public interface IPlugin { } }
namespace Vela.Plugin { public abstract class EntityPluginBase<T> { } }
";

    private static DiagnosticResult Expected(string member, string shape = MutablePluginStateAnalyzer.WritableField, int location = 0) =>
        new DiagnosticResult(Vela.Analyzers.MutablePluginStateAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
            .WithLocation(location)
            .WithArguments(member, shape);

    [Fact]
    public async Task Flags_a_writable_instance_field_on_a_plugin()
    {
        string code = Harness + @"
namespace Demo
{
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin
    {
        private int {|#0:_count|};
        public void Execute(System.IServiceProvider sp) { }
    }
}";
        await Verify.VerifyAnalyzerAsync(code, Expected("_count"));
    }

    [Fact]
    public async Task Flags_a_static_writable_field_on_a_plugin()
    {
        string code = Harness + @"
namespace Demo
{
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin
    {
        private static string {|#0:_cache|};
        public void Execute(System.IServiceProvider sp) { }
    }
}";
        await Verify.VerifyAnalyzerAsync(code, Expected("_cache"));
    }

    [Fact]
    public async Task Does_not_flag_const_readonly_or_static_readonly()
    {
        string code = Harness + @"
namespace Demo
{
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin
    {
        private const int Max = 5;
        private readonly int _id = 1;
        private static readonly string Name = ""x"";
        public void Execute(System.IServiceProvider sp) { }
    }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Flags_a_writable_field_on_a_Vela_base_class_plugin()
    {
        string code = Harness + @"
namespace Demo
{
    using Vela.Plugin;
    public sealed class Account { }
    public sealed class MyPlugin : EntityPluginBase<Account>
    {
        private int {|#0:_count|};
    }
}";
        await Verify.VerifyAnalyzerAsync(code, Expected("_count"));
    }

    [Fact]
    public async Task Flags_a_writable_auto_property_on_a_plugin()
    {
        // #448: the backing field of a writable auto-property is a writable field the sandbox shares across
        // executions, whatever the setter's accessibility and whether it is static.
        string code = Harness + @"
namespace Demo
{
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin
    {
        public int {|#0:Count|} { get; set; }
        public string {|#1:Last|} { get; private set; }
        public static string {|#2:Shared|} { get; set; }
        public void Execute(System.IServiceProvider sp) { }
    }
}";
        await Verify.VerifyAnalyzerAsync(
            code,
            Expected("Count", MutablePluginStateAnalyzer.WritableAutoProperty, 0),
            Expected("Last", MutablePluginStateAnalyzer.WritableAutoProperty, 1),
            Expected("Shared", MutablePluginStateAnalyzer.WritableAutoProperty, 2));
    }

    [Fact]
    public async Task Does_not_flag_get_only_init_only_or_computed_properties()
    {
        // A get-only auto-property is backed by a readonly field, init-only is set once at construction, and a
        // property with accessor bodies stores nothing itself (its field, when writable, is flagged on its own).
        string code = Harness + @"
namespace System.Runtime.CompilerServices { internal static class IsExternalInit { } }
namespace Demo
{
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin
    {
        private readonly int _value = 1;
        public int Id { get; } = 1;
        public string Name { get; init; }
        public int Value { get => _value; set { } }
        public int Twice => _value * 2;
        public int this[int i] { get => i; set { } }
        public void Execute(System.IServiceProvider sp) { }
    }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_writable_auto_property_on_a_non_plugin_type()
    {
        string code = Harness + @"
namespace Demo
{
    public class Settings
    {
        public int Count { get; set; }
    }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Flags_a_readonly_field_holding_a_mutable_collection()
    {
        // #448: readonly freezes the reference, not the contents, so a readonly List/Dictionary/HashSet (or a field
        // typed as a mutable collection interface) still shares its contents across executions.
        string code = Harness + @"
namespace Demo
{
    using System.Collections;
    using System.Collections.Generic;
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin
    {
        private readonly List<string> {|#0:_seen|} = new List<string>();
        private static readonly Dictionary<string, int> {|#1:Counts|} = new Dictionary<string, int>();
        private static readonly IList<string> {|#2:Names|} = new List<string>();
        private readonly Hashtable {|#3:_legacy|} = new Hashtable();
        public void Execute(System.IServiceProvider sp) { }
    }
}";
        await Verify.VerifyAnalyzerAsync(
            code,
            Expected("_seen", MutablePluginStateAnalyzer.ReadOnlyMutableCollection, 0),
            Expected("Counts", MutablePluginStateAnalyzer.ReadOnlyMutableCollection, 1),
            Expected("Names", MutablePluginStateAnalyzer.ReadOnlyMutableCollection, 2),
            Expected("_legacy", MutablePluginStateAnalyzer.ReadOnlyMutableCollection, 3));
    }

    [Fact]
    public async Task Flags_a_readonly_field_holding_a_mutable_non_generic_object_model_or_specialized_collection()
    {
        // The non-generic mutable interfaces and the System.Collections.ObjectModel / Specialized collections are as
        // writable as a List; ReadOnlyCollection (below) is not.
        string code = Harness + @"
namespace System.Collections.Generic { public class PriorityQueue<TElement, TPriority> { } }
namespace Demo
{
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Collections.Specialized;
    using Microsoft.Xrm.Sdk;
    public sealed class ByName : KeyedCollection<string, string> { protected override string GetKeyForItem(string item) => item; }
    public class MyPlugin : IPlugin
    {
        private static readonly IList {|#0:Items|} = new ArrayList();
        private static readonly IDictionary {|#1:Map|} = new Hashtable();
        private readonly Collection<string> {|#2:_collection|} = new Collection<string>();
        private readonly ObservableCollection<string> {|#3:_observable|} = new ObservableCollection<string>();
        private readonly KeyedCollection<string, string> {|#4:_keyed|} = new ByName();
        private readonly PriorityQueue<string, int> {|#5:_queue|} = new PriorityQueue<string, int>();
        private readonly NameValueCollection {|#6:_pairs|} = new NameValueCollection();
        private readonly StringCollection {|#7:_strings|} = new StringCollection();
        private readonly OrderedDictionary {|#8:_ordered|} = new OrderedDictionary();
        private readonly ReadOnlyCollection<string> _view = new List<string>().AsReadOnly();
        public void Execute(System.IServiceProvider sp) { }
    }
}";
        await Verify.VerifyAnalyzerAsync(
            code,
            Expected("Items", MutablePluginStateAnalyzer.ReadOnlyMutableCollection, 0),
            Expected("Map", MutablePluginStateAnalyzer.ReadOnlyMutableCollection, 1),
            Expected("_collection", MutablePluginStateAnalyzer.ReadOnlyMutableCollection, 2),
            Expected("_observable", MutablePluginStateAnalyzer.ReadOnlyMutableCollection, 3),
            Expected("_keyed", MutablePluginStateAnalyzer.ReadOnlyMutableCollection, 4),
            Expected("_queue", MutablePluginStateAnalyzer.ReadOnlyMutableCollection, 5),
            Expected("_pairs", MutablePluginStateAnalyzer.ReadOnlyMutableCollection, 6),
            Expected("_strings", MutablePluginStateAnalyzer.ReadOnlyMutableCollection, 7),
            Expected("_ordered", MutablePluginStateAnalyzer.ReadOnlyMutableCollection, 8));
    }

    [Fact]
    public async Task Does_not_flag_readonly_arrays_read_only_concurrent_or_immutable_collections()
    {
        // Arrays (the usual fixed column lists), read-only views and the concurrent collections (thread-safe by
        // design, the deliberate cache) are left alone; so is a mutable collection on a non-plugin type.
        string code = Harness + @"
namespace Demo
{
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using Microsoft.Xrm.Sdk;
    public class MyPlugin : IPlugin
    {
        private static readonly string[] Columns = { ""name"" };
        private static readonly IReadOnlyList<string> Names = new List<string>();
        private static readonly IReadOnlyDictionary<string, int> Map = new Dictionary<string, int>();
        private static readonly ReadOnlyCollection<string> View = new List<string>().AsReadOnly();
        private static readonly ConcurrentDictionary<string, int> Cache = new ConcurrentDictionary<string, int>();
        public void Execute(System.IServiceProvider sp) { }
    }
    public class Helper
    {
        private readonly List<string> _items = new List<string>();
    }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Flags_a_primary_constructor_parameter_captured_by_a_member()
    {
        // #448: a primary-constructor parameter read by a method is stored by the compiler in a writable field.
        string code = Harness + @"
namespace Demo
{
    using Microsoft.Xrm.Sdk;
    public class MyPlugin(string {|#0:unsecure|}, string secure) : IPlugin
    {
        public void Execute(System.IServiceProvider sp) { System.GC.KeepAlive(unsecure); }
    }
}";
        await Verify.VerifyAnalyzerAsync(code, Expected("unsecure", MutablePluginStateAnalyzer.CapturedParameter));
    }

    [Fact]
    public async Task Flags_a_primary_constructor_parameter_captured_by_several_members_once()
    {
        string code = Harness + @"
namespace Demo
{
    using Vela.Plugin;
    public sealed class Account { }
    public sealed class MyPlugin(string {|#0:config|}) : EntityPluginBase<Account>
    {
        public string Config => config;
        public void Reset() { config = null; }
    }
}";
        await Verify.VerifyAnalyzerAsync(code, Expected("config", MutablePluginStateAnalyzer.CapturedParameter));
    }

    [Fact]
    public async Task Does_not_flag_a_primary_constructor_parameter_used_only_at_construction()
    {
        // A use in a field / property initializer or a base-constructor argument runs once at construction and
        // stores nothing; an unused parameter is not captured either.
        string code = Harness + @"
namespace Demo
{
    using Microsoft.Xrm.Sdk;
    public abstract class Base : IPlugin
    {
        protected Base(string name) { }
        public void Execute(System.IServiceProvider sp) { }
    }
    public class MyPlugin(string unsecure, string secure, string unused) : Base(secure)
    {
        private readonly string _config = unsecure;
        public string Config { get; } = unsecure;
    }
    public class Helper(string value)
    {
        public string Value => value;
    }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_field_on_a_non_plugin_type()
    {
        string code = Harness + @"
namespace Demo
{
    public class Helper
    {
        private int _count;
    }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_a_type_deriving_from_a_same_named_PluginBase_outside_Vela()
    {
        // A-77: the Vela bases are matched by symbol, not by simple name.
        string code = Harness + @"
namespace Other { public abstract class PluginBase { } }
namespace Demo
{
    public class Worker : Other.PluginBase
    {
        private int _count;
    }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }

    [Fact]
    public async Task Does_not_flag_anything_when_neither_the_SDK_nor_Vela_is_referenced()
    {
        // Compilation guard: no IPlugin and no Vela base in the compilation -> nothing to analyze.
        string code = @"
namespace Vela.Lookalike { public abstract class EntityPluginBase<T> { } }
namespace Demo
{
    public class Worker : Vela.Lookalike.EntityPluginBase<int>
    {
        private int _count;
    }
}";
        await Verify.VerifyAnalyzerAsync(code);
    }
}
