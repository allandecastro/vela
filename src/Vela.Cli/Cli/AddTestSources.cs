using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Vela.Cli;

/// <summary>
/// Reads the plugin and Custom API classes a <c>vela add test</c> test is written for, from the source of the projects
/// the test project references (else the solution), so the generated test uses the real class, namespace, steps, unique
/// name and request type instead of names guessed from the command line. Text-based (no compilation): it reads the
/// shapes <c>vela add</c> and <c>vela init</c> generate, with comments and literal contents masked.
/// </summary>
internal static class AddTestSources
{
    /// <summary>A class declared in a source file.</summary>
    internal sealed record SourceClass(string Name, string Namespace, string BaseList, string Body, string FileText, string FilePath);

    /// <summary>One <c>RegisterStep()</c> chain: its message, the TRequest of an <c>On&lt;TRequest&gt;()</c> trigger, its FilterOn members.</summary>
    internal sealed record PluginStep(string Message, string? RequestType, string[] Filters);

    private static readonly Regex ProjectReference = new(@"<ProjectReference\s+Include\s*=\s*""(?<path>[^""]+)""", RegexOptions.Compiled);
    private static readonly Regex NamespaceDecl = new(@"^\s*namespace\s+(?<ns>[\w\.]+)", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex ClassDecl = new(@"\bclass\s+(?<name>[A-Za-z_]\w*)(?<tail>[^{;]*)", RegexOptions.Compiled);
    private static readonly Regex StepChain = new(
        @"RegisterStep\s*\(\s*\)\s*\.\s*On(?:(?<typed>[A-Za-z]\w*)|\s*<\s*(?<request>[\w\.]+)\s*>)\s*\(\s*\)(?<rest>[^;]*);",
        RegexOptions.Compiled);
    private static readonly Regex EntityBase = new(@"EntityPluginBase\s*<\s*(?<entity>[\w\.]+)\s*>", RegexOptions.Compiled);
    // The base type, whether the class declares a primary constructor (": CustomApiBase(u, s)") or a conventional one.
    private static readonly Regex CustomApiBase = new(@":\s*(?:[\w\.]+\.)?CustomApiBase\b\s*(?:<\s*(?<request>[\w\.]+)\s*,\s*(?<response>[\w\.]+)\s*>)?", RegexOptions.Compiled);
    private static readonly Regex RegisterCustomApiCall = new(@"\bRegisterCustomApi\s*\(\s*(?:(?<literal>"")|(?<name>[A-Za-z_][\w\.]*))", RegexOptions.Compiled);

    /// <summary>
    /// The directories whose sources are searched: those of the projects <paramref name="projectFile"/> references, or,
    /// when it references none that exist, the solution root (see <see cref="AddCommand.FindSolutionRoot"/>).
    /// </summary>
    internal static string[] SearchRoots(string projectFile, string projectDir)
    {
        List<string> roots = new List<string>();
        string project;
        try
        {
            project = File.ReadAllText(projectFile);
        }
        catch (IOException)
        {
            project = string.Empty;
        }

        foreach (Match reference in ProjectReference.Matches(project))
        {
            string relative = reference.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            string? dir = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(projectDir, relative)));
            if (dir is not null && Directory.Exists(dir) && !roots.Contains(dir, StringComparer.Ordinal))
            {
                roots.Add(dir);
            }
        }

        return roots.Count > 0 ? roots.ToArray() : [AddCommand.FindSolutionRoot(projectDir)];
    }

    /// <summary>Every class declared in the C# files under <paramref name="roots"/>, skipping build output.</summary>
    internal static List<SourceClass> FindClasses(IEnumerable<string> roots)
    {
        List<SourceClass> classes = new List<SourceClass>();
        string obj = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        string bin = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
        foreach (string root in roots)
        {
            foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(obj, StringComparison.Ordinal) || file.Contains(bin, StringComparison.Ordinal))
                {
                    continue;
                }

                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (IOException)
                {
                    continue;
                }

                string masked = StepInjector.MaskCommentsAndLiterals(text);
                Match ns = NamespaceDecl.Match(masked);
                MatchCollection declarations = ClassDecl.Matches(masked);
                for (int i = 0; i < declarations.Count; i++)
                {
                    Match declaration = declarations[i];
                    int end = i + 1 < declarations.Count ? declarations[i + 1].Index : text.Length;
                    classes.Add(new SourceClass(
                        declaration.Groups["name"].Value,
                        ns.Success ? ns.Groups["ns"].Value : string.Empty,
                        declaration.Groups["tail"].Value,
                        text.Substring(declaration.Index, end - declaration.Index),
                        text,
                        file));
                }
            }
        }

        return classes;
    }

    /// <summary>The entity type of an <c>EntityPluginBase&lt;TEntity&gt;</c> class, or null.</summary>
    internal static string? PluginEntity(SourceClass type)
    {
        Match match = EntityBase.Match(type.BaseList);
        return match.Success ? match.Groups["entity"].Value : null;
    }

    /// <summary>The steps a plugin class registers, in source order.</summary>
    internal static List<PluginStep> ReadSteps(SourceClass type)
    {
        List<PluginStep> steps = new List<PluginStep>();
        foreach (Match chain in StepChain.Matches(StepInjector.MaskCommentsAndLiterals(type.Body)))
        {
            string? request = chain.Groups["request"].Success ? chain.Groups["request"].Value : null;
            string message = request is null ? chain.Groups["typed"].Value : MessageOfRequest(request);
            steps.Add(new PluginStep(message, request, StepInjector.FilterColumns(chain.Groups["rest"].Value).ToArray()));
        }

        return steps;
    }

    // "Microsoft.Crm.Sdk.Messages.SetStateRequest" -> "SetState".
    private static string MessageOfRequest(string request)
    {
        string simple = request.Substring(request.LastIndexOf('.') + 1);
        return simple.EndsWith("Request", StringComparison.Ordinal) && simple.Length > "Request".Length
            ? simple.Substring(0, simple.Length - "Request".Length)
            : simple;
    }

    /// <summary>True when the class derives from <c>CustomApiBase</c> or <c>CustomApiBase&lt;TRequest, TResponse&gt;</c>; the request type of the typed form.</summary>
    internal static bool IsCustomApi(SourceClass type, out string? requestType)
    {
        Match match = CustomApiBase.Match(type.BaseList);
        requestType = match.Success && match.Groups["request"].Success ? match.Groups["request"].Value : null;
        return match.Success;
    }

    /// <summary>
    /// The unique name a Custom API class registers: the string literal passed to <c>RegisterCustomApi(...)</c>, or the
    /// <c>const string</c> it names (in the same file). Null when it is computed some other way.
    /// </summary>
    internal static string? CustomApiUniqueName(SourceClass type)
    {
        Match call = RegisterCustomApiCall.Match(StepInjector.MaskCommentsAndLiterals(type.Body));
        if (!call.Success)
        {
            return null;
        }

        if (call.Groups["literal"].Success)
        {
            int start = call.Groups["literal"].Index + 1;
            int end = type.Body.IndexOf('"', start);
            return end > start ? type.Body.Substring(start, end - start) : null;
        }

        string constant = call.Groups["name"].Value;
        constant = constant.Substring(constant.LastIndexOf('.') + 1);
        Match value = Regex.Match(type.FileText, $@"\bconst\s+string\s+{Regex.Escape(constant)}\s*=\s*""(?<value>[^""]*)""");
        return value.Success ? value.Groups["value"].Value : null;
    }
}
