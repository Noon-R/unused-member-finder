# 未使用メンバー検査ツール 実装計画

C# のソリューション（`.sln` 1つ）を対象に、Roslyn で「実際にどこからも使われていないメンバー（型・プロパティ・メソッド・フィールド等）」を洗い出す CLI ツール。Python から `subprocess` で exe を起動して実行する。

---

## 前提

- 検査対象は **`.sln` が1つ**。フォルダ内に多階層で `.cs` が散らばっているが、走査するのは `.cs` ではなく `.sln`（理由は下記）。
- アプリ内部のコードなので、ソリューション内に全利用箇所が存在する。よって **「参照ゼロ = 未使用」と判定してよい**（public メンバーも検査対象にできる）。
- 検査する側のマシンに **.NET SDK が必要**（MSBuildWorkspace が対象プロジェクトをビルド文脈として読むため）。

### なぜ `.cs` を直接集めずに `.sln` を開くのか

Roslyn の `FindReferencesAsync` が参照を正しく追うには、正しいコンパイル文脈（プロジェクト参照・NuGet・ターゲットフレームワーク）が必要。`.cs` を単純にかき集めてアドホックにコンパイルすると参照解決が崩れ、「本当は使われているのに参照ゼロに見える」誤検出が大量に出る。`MSBuildWorkspace` で `.sln` を開けばこの文脈が正しく構築される。これが「漏れなく・誤検出なく」の前提条件。

---

## 全体構成

```
unused-member-finder/
├─ UnusedMemberFinder/            ← C# CLI プロジェクト（exe 本体）
│   ├─ Program.cs
│   ├─ MemberAnalyzer.cs          ← 検査ロジック
│   ├─ SkipRules.cs               ← 削除すると危険なものの除外ルール
│   └─ UnusedMemberFinder.csproj
└─ runner/
    └─ run_analysis.py            ← Python オーケストレーター
```

### 役割分担

- **exe (C#)**: 1ソリューションを受け取り、未使用メンバー一覧を JSON で stdout に出力するだけに徹する。
- **Python**: exe を起動し、JSON を受け取って集約・レポート化・終了コード判定を行う。

---

## Phase 1: exe 本体（最優先・ここで精度が決まる）

> Python 連携より先にここを固める。検査精度が全体の品質を決めるため。

### 1-1. プロジェクト作成

```bash
dotnet new console -n UnusedMemberFinder
cd UnusedMemberFinder
dotnet add package Microsoft.Build.Locator
dotnet add package Microsoft.CodeAnalysis.Workspaces.MSBuild
```

### 1-2. CLI インターフェース

| 引数 | 必須 | 説明 |
|---|---|---|
| `--solution <path>` | ○ | 検査対象 `.sln` のパス |
| `--include-public` | | public メンバーも検査対象に含める |
| `--out <path>` | | JSON 出力先（未指定なら stdout） |

- 出力: JSON（stdout もしくは `--out`）
- 終了コード: 0 = 成功、非0 = エラー
- workspace の警告は **stderr** に出す（stdout の JSON を汚さない）

### 1-3. 検査ロジック（`MemberAnalyzer.cs`）

判定方針: **「自分の宣言箇所（span）を除いた参照がゼロなら未使用」**。
ファイル単位ではなく span 単位で宣言箇所を除外すること（同一ファイル内の別メソッドからの利用を誤って未使用扱いしないため）。

```csharp
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using System.Text.Json;

MSBuildLocator.RegisterDefaults();

string slnPath = GetArg(args, "--solution")
    ?? throw new ArgumentException("--solution required");
bool includePublic = args.Contains("--include-public");

using var ws = MSBuildWorkspace.Create();
ws.WorkspaceFailed += (_, e) =>
    Console.Error.WriteLine($"[workspace] {e.Diagnostic.Message}");

var solution = (await ws.OpenSolutionAsync(slnPath)).Solution;
var results = new List<object>();

foreach (var project in solution.Projects)
{
    var comp = await project.GetCompilationAsync();
    if (comp is null) continue;

    foreach (var type in GetAllTypes(comp.GlobalNamespace))
    {
        var members = new ISymbol[] { type }
            .Concat(type.GetMembers())
            .Where(m => !m.IsImplicitlyDeclared)
            .Where(m => m.Locations.Any(l => l.IsInSource));

        foreach (var sym in members)
        {
            if (SkipRules.ShouldSkip(sym, includePublic)) continue;

            var refs = await SymbolFinder.FindReferencesAsync(sym, solution);

            var declSpans = sym.DeclaringSyntaxReferences
                .Select(r => (r.SyntaxTree.FilePath, r.Span))
                .ToHashSet();

            int useCount = refs
                .SelectMany(r => r.Locations)
                .Where(l => l.Location.IsInSource)
                .Count(l => !declSpans.Contains(
                    (l.Location.SourceTree?.FilePath, l.Location.SourceSpan)));

            if (useCount == 0)
            {
                var loc = sym.Locations.First(l => l.IsInSource).GetLineSpan();
                results.Add(new {
                    project = project.Name,
                    kind = sym.Kind.ToString(),
                    name = sym.ToDisplayString(),
                    accessibility = sym.DeclaredAccessibility.ToString(),
                    file = loc.Path,
                    line = loc.StartLinePosition.Line + 1
                });
            }
        }
    }
}

var json = JsonSerializer.Serialize(
    new { solution = slnPath, unused = results },
    new JsonSerializerOptions { WriteIndented = true });

string? outPath = GetArg(args, "--out");
if (outPath is null) Console.WriteLine(json);
else File.WriteAllText(outPath, json);
```

ヘルパーとして以下を実装:
- `GetAllTypes(INamespaceSymbol)`: ネストした型・名前空間を再帰的に列挙して `INamedTypeSymbol` を全部返す。
- `GetArg(string[], string)`: `--key value` 形式の引数取得。

### 1-4. 除外ルール（`SkipRules.cs`）

参照ゼロでも **消すと壊れる / 未使用とは限らない** もの。ここは実データを見ながら育てる。

```csharp
static bool ShouldSkip(ISymbol sym, bool includePublic)
{
    // public を検査対象に含めないモード
    if (!includePublic && sym.DeclaredAccessibility == Accessibility.Public)
        return true;

    // エントリポイント
    if (sym is IMethodSymbol { Name: "Main", IsStatic: true })
        return true;

    // override / インターフェース実装（framework や外部から呼ばれうる）
    if (sym is IMethodSymbol m && (m.IsOverride || IsInterfaceImpl(m)))
        return true;

    // 属性付き（[Obsolete], [Fact], [Test], シリアライズ属性 等）
    if (sym.GetAttributes().Length > 0)
        return true;

    // 自動プロパティのバッキングフィールド等は IsImplicitlyDeclared で既に除外済み
    return false;
}
```

> 最初の実行では framework 呼び出し・リフレクション・DI・XAML バインディング経由で
> ほぼ確実に誤検出が出る。ここを見ながら除外条件とオプションを調整する。

---

## Phase 2: 除外ルールの実データ調整

1. 小さな既知のソリューションで実行し、以下を確認:
   - 既知の未使用メンバーがちゃんと出るか
   - 使われているメンバーを誤検出しないか
2. 実プロジェクトで実行し、誤検出パターンを洗い出して `SkipRules` に反映:
   - リフレクション (`GetProperty("X")` 等) で触っているもの
   - シリアライズ / デシリアライズ対象
   - DI コンテナ経由
   - XAML / データバインディング経由
   - イベントハンドラ・ライフサイクルメソッド

> これらは Roslyn の意味解析の外なので、JSON 出力後に文字列 grep で裏取りすると安全。

---

## Phase 3: Python オーケストレーター（`runner/run_analysis.py`）

`.sln` は1つなので走査はシンプル（複数 sln 対応は不要）。

```python
#!/usr/bin/env python3
"""未使用メンバー検査オーケストレーター（.sln 1つ想定）"""
import argparse
import json
import subprocess
import sys
from pathlib import Path


def run_exe(exe: Path, sln: Path, extra_args: list[str]) -> dict:
    cmd = [str(exe), "--solution", str(sln), *extra_args]
    proc = subprocess.run(
        cmd,
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=1800,  # 大規模ソリューション対策
    )

    if proc.stderr:
        print(f"[stderr]\n{proc.stderr}", file=sys.stderr)

    if proc.returncode != 0:
        return {"error": proc.stderr or f"exit code {proc.returncode}"}

    try:
        return json.loads(proc.stdout)
    except json.JSONDecodeError as e:
        return {"error": f"JSON parse failed: {e}\nstdout head:\n{proc.stdout[:500]}"}


def main():
    p = argparse.ArgumentParser(description="未使用メンバー検査")
    p.add_argument("solution", type=Path, help="検査対象の .sln パス")
    p.add_argument("--exe", type=Path, required=True,
                   help="UnusedMemberFinder.exe のパス")
    p.add_argument("--out", type=Path, default=Path("unused_report.json"))
    p.add_argument("--include-public", action="store_true")
    p.add_argument("--fail-on-found", action="store_true",
                   help="未使用が1件でもあれば exit 1（CI 用）")
    args = p.parse_args()

    if not args.solution.exists():
        print(f"sln が見つかりません: {args.solution}", file=sys.stderr)
        sys.exit(1)

    extra = ["--include-public"] if args.include_public else []

    print(f"検査開始: {args.solution.name}")
    result = run_exe(args.exe, args.solution, extra)

    if "error" in result:
        print(f"エラー: {result['error']}", file=sys.stderr)
        sys.exit(2)

    unused = result.get("unused", [])

    # JSON レポート出力
    args.out.write_text(
        json.dumps(result, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )

    # コンソール要約（file:line 形式で grep / エディタ連携しやすく）
    print(f"\n未使用候補 {len(unused)} 件 → {args.out}\n")
    for u in unused:
        print(f"  {u['file']}:{u['line']}  "
              f"{u['accessibility']} {u['kind']} {u['name']}")

    if args.fail_on_found and unused:
        sys.exit(1)


if __name__ == "__main__":
    main()
```

### 実行例

```bash
python runner/run_analysis.py ./MyApp.sln \
    --exe ./publish/UnusedMemberFinder.exe \
    --include-public
```

---

## Phase 4: 配布・仕上げ

### exe の publish

self-contained single-file にすると Python から exe 一個を叩くだけで済む:

```bash
dotnet publish UnusedMemberFinder -c Release -r win-x64 \
    --self-contained -p:PublishSingleFile=true -o ./publish
```

> 注意: self-contained でも、**検査対象を読む側のマシンには .NET SDK が必要**
> （MSBuildWorkspace が対象プロジェクトをビルド文脈として解決するため）。

### 仕上げ項目

- レポートは JSON + `file:line` 形式の両方を出す（grep / エディタ連携用）。
- CI 組み込み時は `--fail-on-found` で未使用検出時に exit 1。
- 未使用候補が出たら一括削除せず、**1つずつ消して再ビルド・再検査**するのが最も確実
  （特にプロパティとそのバッキングフィールドのような連鎖的未使用があるため）。

---

## 実装順序まとめ

| Phase | 内容 | ゴール |
|---|---|---|
| 1 | exe 本体（単一 sln → JSON） | 既知の未使用が出る / 誤検出しないことを小規模で確認 |
| 2 | 除外ルール調整 | 実プロジェクトで誤検出を潰す |
| 3 | Python オーケストレーター | sln 指定 → レポート出力 |
| 4 | publish・仕上げ | single-file exe / CI 連携 / レポート整形 |

---

## 既知の限界（ツールで検出できないもの）

以下は「参照ゼロ」と表示されても未使用とは限らない。削除前に文字列 grep で確認すること。

- リフレクション (`typeof(T).GetProperty("X")` 等)
- 文字列ベースの動的アクセス
- シリアライズ / デシリアライズ対象
- DI コンテナ経由のインスタンス化・注入
- XAML / データバインディング経由のプロパティアクセス
- framework から呼ばれるエントリポイント・イベントハンドラ・ライフサイクルメソッド
