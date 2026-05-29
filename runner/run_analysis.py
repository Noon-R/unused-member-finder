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


def print_summary(unused: list[dict]) -> None:
    if not unused:
        print("未使用候補: 0 件")
        return

    # プロジェクト別に集計
    by_project: dict[str, list[dict]] = {}
    for u in unused:
        by_project.setdefault(u["project"], []).append(u)

    for project, members in sorted(by_project.items()):
        print(f"\n  [{project}]  {len(members)} 件")
        for u in members:
            print(f"    {u['file']}:{u['line']}  "
                  f"{u['accessibility']} {u['kind']}  {u['name']}")


def main() -> None:
    p = argparse.ArgumentParser(description="未使用メンバー検査")
    p.add_argument("solution", type=Path, help="検査対象の .sln パス")
    p.add_argument("--exe", type=Path, required=True,
                   help="UnusedMemberFinder.exe のパス")
    p.add_argument("--out", type=Path, default=Path("unused_report.json"))
    p.add_argument("--include-public", action="store_true",
                   help="public メンバーも検査対象に含める")
    p.add_argument("--fail-on-found", action="store_true",
                   help="未使用が1件でもあれば exit 1（CI 用）")
    args = p.parse_args()

    if not args.solution.exists():
        print(f"sln が見つかりません: {args.solution}", file=sys.stderr)
        sys.exit(1)

    if not args.exe.exists():
        print(f"exe が見つかりません: {args.exe}", file=sys.stderr)
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

    # コンソール要約
    print(f"\n未使用候補 {len(unused)} 件 → {args.out}")
    print_summary(unused)

    if args.fail_on_found and unused:
        sys.exit(1)


if __name__ == "__main__":
    main()
