// Regenerates the step builder surface (StepApi.cs, StepBuilder.Impl.cs) from MessageModel.cs.
//
//   dotnet run --project tools/codegen/Vela.StepApiGen              write the files
//   dotnet run --project tools/codegen/Vela.StepApiGen -- --check   exit 1 if a committed file is out of date (CI)
//   ... -- --root <repo>                                            repo root (default: nearest parent with Vela.sln)
using Vela.StepApiGen;

bool check = false;
string? root = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--check":
            check = true;
            break;
        case "--root" when i + 1 < args.Length:
            root = args[++i];
            break;
        default:
            Console.Error.WriteLine($"Unknown argument '{args[i]}'. Usage: [--check] [--root <repo>]");
            return 2;
    }
}

root ??= FindRepoRoot(Environment.CurrentDirectory) ?? FindRepoRoot(AppContext.BaseDirectory);
if (root is null || !File.Exists(Path.Combine(root, "Vela.sln")))
{
    Console.Error.WriteLine("Could not locate the repo root (a directory containing Vela.sln). Pass --root <repo>.");
    return 2;
}

var outputs = new (string Path, string Content)[]
{
    (StepApiEmitter.RelativePath, StepApiEmitter.Emit()),
    (StepBuilderImplEmitter.RelativePath, StepBuilderImplEmitter.Emit()),
    (TypedTriggersEmitter.RelativePath, TypedTriggersEmitter.Emit()),
};

int stale = 0;
foreach (var (relative, content) in outputs)
{
    string path = Path.Combine(root, relative);
    // A Windows checkout may carry CRLF (no .gitattributes); compare line-ending agnostic.
    string? current = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : null;
    if (current == content)
    {
        Console.WriteLine($"up to date  {relative}");
        continue;
    }

    if (check)
    {
        stale++;
        Console.Error.WriteLine($"OUT OF DATE {relative}{FirstDifference(current, content)}");
    }
    else
    {
        File.WriteAllText(path, content);
        Console.WriteLine($"written     {relative}");
    }
}

if (stale > 0)
{
    Console.Error.WriteLine("The generated step builder files differ from tools/codegen/Vela.StepApiGen output. "
        + "Edit MessageModel.cs (not the generated files), then run: dotnet run --project tools/codegen/Vela.StepApiGen");
    return 1;
}
return 0;

static string? FindRepoRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Vela.sln")))
        {
            return dir.FullName;
        }
    }
    return null;
}

static string FirstDifference(string? current, string expected)
{
    if (current is null)
    {
        return " (missing)";
    }
    string[] a = current.Split('\n'), b = expected.Split('\n');
    for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
    {
        string? x = i < a.Length ? a[i] : null, y = i < b.Length ? b[i] : null;
        if (x != y)
        {
            return $" (first difference at line {i + 1})\n  committed: {x ?? "<eof>"}\n  generated: {y ?? "<eof>"}";
        }
    }
    return "";
}
