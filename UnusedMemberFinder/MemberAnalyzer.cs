using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace UnusedMemberFinder;

internal static class MemberAnalyzer
{
    public static async Task<List<UnusedMember>> AnalyzeAsync(
        Solution solution,
        bool includePublic,
        string? folderFilter = null,
        Action<string>? log = null)
    {
        // フォルダフィルタを正規化（末尾セパレータを統一）
        string? normalizedFolder = folderFilter is null
            ? null
            : Path.GetFullPath(folderFilter).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
              + Path.DirectorySeparatorChar;

        if (normalizedFolder is not null)
            log?.Invoke($"[info] フォルダフィルタ: {normalizedFolder}");

        var results = new List<UnusedMember>();

        foreach (var project in solution.Projects)
        {
            log?.Invoke($"[info] プロジェクト解析中: {project.Name}");
            var comp = await project.GetCompilationAsync();
            if (comp is null)
            {
                log?.Invoke($"[warn] コンパイル取得失敗: {project.Name}");
                continue;
            }

            foreach (var type in GetAllTypes(comp.GlobalNamespace))
            {
                var members = new ISymbol[] { type }
                    .Concat(type.GetMembers())
                    .Where(m => !m.IsImplicitlyDeclared)
                    .Where(m => m.Locations.Any(l => l.IsInSource));

                foreach (var sym in members)
                {
                    if (SkipRules.ShouldSkip(sym, includePublic)) continue;

                    // 宣言ファイルがフォルダ内にない場合はスキップ
                    if (normalizedFolder is not null && !IsDeclaredUnderFolder(sym, normalizedFolder))
                        continue;

                    var refs = await SymbolFinder.FindReferencesAsync(sym, solution);

                    var declSpans = sym.DeclaringSyntaxReferences
                        .Select(r => (r.SyntaxTree.FilePath, r.Span))
                        .ToHashSet();

                    int useCount = refs
                        .SelectMany(r => r.Locations)
                        .Where(l => l.Location.IsInSource)
                        .Count(l => !declSpans.Contains(
                            (l.Location.SourceTree?.FilePath ?? string.Empty,
                             l.Location.SourceSpan)));

                    if (useCount == 0)
                    {
                        var loc = sym.Locations.First(l => l.IsInSource).GetLineSpan();
                        results.Add(new UnusedMember(
                            Project: project.Name,
                            Kind: sym.Kind.ToString(),
                            Name: sym.ToDisplayString(),
                            Accessibility: sym.DeclaredAccessibility.ToString(),
                            File: loc.Path,
                            Line: loc.StartLinePosition.Line + 1
                        ));
                    }
                }
            }
        }

        return results;
    }

    private static bool IsDeclaredUnderFolder(ISymbol sym, string normalizedFolder)
    {
        return sym.Locations
            .Where(l => l.IsInSource && l.SourceTree is not null)
            .Any(l =>
            {
                string filePath = Path.GetFullPath(l.SourceTree!.FilePath);
                return filePath.StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase);
            });
    }

    private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in GetNestedTypes(type))
                yield return nested;
        }

        foreach (var childNs in ns.GetNamespaceMembers())
        foreach (var type in GetAllTypes(childNs))
            yield return type;
    }

    private static IEnumerable<INamedTypeSymbol> GetNestedTypes(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deepNested in GetNestedTypes(nested))
                yield return deepNested;
        }
    }
}

internal record UnusedMember(
    string Project,
    string Kind,
    string Name,
    string Accessibility,
    string File,
    int Line);
