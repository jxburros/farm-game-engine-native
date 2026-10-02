#!/usr/bin/env python3
"""Checks the basic .editorconfig rules for every committed text file, in the languages that
have no formatter in CI (F#, scripts, XAML, project files, Markdown, YAML, JSON, …): UTF-8, LF line
endings, a final newline, no trailing whitespace, and no tab indentation in F#.

C# is checked by `dotnet format` and Rust by `cargo fmt`; this script skips neither, since the
rules are the same. Generated and vendored files are skipped (see SKIP).

  python3 tools/ci/text-style.py          # list the problems, exit 1 when there are any
  python3 tools/ci/text-style.py --fix    # fix line endings, trailing whitespace and final newlines
"""
import pathlib
import subprocess
import sys

root = pathlib.Path(__file__).resolve().parents[2]

TEXT = {
    ".fs", ".fsx", ".fsi", ".fsproj", ".cs", ".csproj", ".axaml", ".props", ".targets", ".sln",
    ".rs", ".toml", ".sh", ".py", ".mjs", ".js", ".ts", ".css", ".html", ".md", ".yml", ".yaml",
    ".json", ".fbs", ".hbs", ".xml", ".manifest", ".desktop", ".txt",
}
# Generated or third-party text that is checked in byte for byte.
SKIP_PREFIXES = (
    "crates/farm-cart-schema/src/",  # flatc output (tools/codegen/check.sh)
    "fixtures/",  # goldens and recorded test data
    "tools/player-licenses/plugin-guest/",  # upstream license texts
    "tools/editor-licenses/texts/",  # upstream license texts
    "assets/fonts/",  # upstream font licenses
)
SKIP_FILES = {
    "tools/player-licenses/THIRD-PARTY.txt",  # generated (generate.sh cleans it)
    "tools/editor-licenses/THIRD-PARTY-dotnet.txt",  # generated
}


def tracked() -> list[str]:
    out = subprocess.run(["git", "-C", str(root), "ls-files", "-z"], check=True, capture_output=True).stdout
    return [p for p in out.decode().split("\0") if p]


def main() -> int:
    fix = "--fix" in sys.argv[1:]
    problems = []
    for name in tracked():
        path = root / name
        if pathlib.PurePosixPath(name).suffix.lower() not in TEXT or name in SKIP_FILES:
            continue
        if name.startswith(SKIP_PREFIXES) or not path.is_file():
            continue
        data = path.read_bytes()
        try:
            text = data.decode("utf-8")
        except UnicodeDecodeError:
            problems.append(f"{name}: not UTF-8")
            continue
        found = []
        if "\r" in text:
            found.append("CRLF line endings")
        lines = text.replace("\r\n", "\n").split("\n")
        trailing = [i + 1 for i, line in enumerate(lines) if line != line.rstrip(" \t")]
        if trailing:
            found.append(f"trailing whitespace on line {trailing[0]}" + (f" (+{len(trailing) - 1} more)" if len(trailing) > 1 else ""))
        if text and not text.endswith("\n"):
            found.append("no final newline")
        if name.endswith((".fs", ".fsx", ".fsi")):
            tabs = [i + 1 for i, line in enumerate(lines) if "\t" in line[: len(line) - len(line.lstrip())]]
            if tabs:
                found.append(f"tab indentation on line {tabs[0]}")
        if found and fix:
            fixed = "\n".join(line.rstrip(" \t") for line in lines).rstrip("\n") + "\n"
            path.write_bytes(fixed.encode("utf-8"))
        problems.extend(f"{name}: {problem}" for problem in found)
    for problem in problems:
        print(problem)
    if problems and not fix:
        print(f"{len(problems)} problem(s); fix them, or run python3 tools/ci/text-style.py --fix", file=sys.stderr)
        return 1
    if not problems:
        print("Text files are LF, UTF-8, with final newlines and no trailing whitespace.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
