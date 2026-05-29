using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using System.Text.Json;
using UnusedMemberFinder;

MSBuildLocator.RegisterDefaults();

string? slnPath = GetArg(args, "--solution");
if (slnPath is null)
{
    Console.Error.WriteLine("エラー: --solution <path> が必要です");
    return 1;
}

if (!File.Exists(slnPath))
{
    Console.Error.WriteLine($"エラー: ファイルが見つかりません: {slnPath}");
    return 1;
}

bool includePublic = args.Contains("--include-public");
string? outPath = GetArg(args, "--out");
string? folder = GetArg(args, "--folder");

using var ws = MSBuildWorkspace.Create();
ws.RegisterWorkspaceFailedHandler(e =>
    Console.Error.WriteLine($"[workspace] {e.Diagnostic.Message}"));

Console.Error.WriteLine($"[info] ソリューションを開いています: {slnPath}");
var solution = await ws.OpenSolutionAsync(slnPath);

Console.Error.WriteLine("[info] 解析を開始します...");
var unused = await MemberAnalyzer.AnalyzeAsync(
    solution,
    includePublic,
    folderFilter: folder,
    log: msg => Console.Error.WriteLine(msg));

Console.Error.WriteLine($"[info] 完了: {unused.Count} 件の未使用候補を検出");

var output = new
{
    solution = Path.GetFullPath(slnPath),
    includePublic,
    folderFilter = folder is null ? null : Path.GetFullPath(folder),
    analyzedAt = DateTime.UtcNow.ToString("o"),
    unusedCount = unused.Count,
    unused = unused.Select(u => new
    {
        project = u.Project,
        kind = u.Kind,
        name = u.Name,
        accessibility = u.Accessibility,
        file = u.File,
        line = u.Line
    })
};

var json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });

if (outPath is null)
    Console.WriteLine(json);
else
{
    File.WriteAllText(outPath, json);
    Console.Error.WriteLine($"[info] JSON を書き込みました: {outPath}");
}

return 0;

static string? GetArg(string[] args, string key)
{
    int idx = Array.IndexOf(args, key);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}
