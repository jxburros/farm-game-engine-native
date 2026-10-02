#!/usr/bin/env python3
"""Line-coverage summary and minimums for CI (thresholds in tools/coverage/thresholds.json).

    tools/coverage/check.py dotnet TestResults/        every coverage.cobertura.xml below the
                                                       folder (coverlet, one per test project),
                                                       merged per assembly
    tools/coverage/check.py rust llvm-cov.json         `cargo llvm-cov report --json
                                                       --summary-only`, summed per crate

Prints a Markdown table (also appended to $GITHUB_STEP_SUMMARY when set) and exits 1 when an
assembly or crate with a minimum is below it, or is missing from the report. Units without a
minimum are reported but not enforced. Raise a minimum when coverage has grown past it; lower
one only with a reason in the commit.
"""
import json
import os
import pathlib
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict

HERE = pathlib.Path(__file__).resolve().parent


def dotnet(folder: pathlib.Path) -> dict[str, tuple[int, int]]:
    """Covered and total lines per assembly. A line counts once per assembly even when several
    test projects (or several compiler-generated classes) report it; it is covered when any
    report hit it."""
    hits: dict[str, dict[tuple[str, int], bool]] = defaultdict(dict)
    reports = sorted(folder.rglob("coverage.cobertura.xml"))
    if not reports:
        sys.exit(f"no coverage.cobertura.xml under {folder}")
    for report in reports:
        root = ET.parse(report).getroot()
        for package in root.iter("package"):
            lines = hits[package.get("name", "?")]
            for cls in package.iter("class"):
                filename = cls.get("filename", "")
                for line in cls.iter("line"):
                    key = (filename, int(line.get("number", "0")))
                    lines[key] = lines.get(key, False) or int(line.get("hits", "0")) > 0
    return {name: (sum(lines.values()), len(lines)) for name, lines in hits.items()}


def rust(summary: pathlib.Path) -> dict[str, tuple[int, int]]:
    """Covered and total lines per workspace crate (crates/<name>/...), plus "total"."""
    data = json.loads(summary.read_text())["data"][0]
    crates: dict[str, list[int]] = defaultdict(lambda: [0, 0])
    for entry in data["files"]:
        parts = pathlib.PurePath(entry["filename"]).parts
        if "crates" not in parts:
            continue
        name = parts[parts.index("crates") + 1]
        lines = entry["summary"]["lines"]
        crates[name][0] += lines["covered"]
        crates[name][1] += lines["count"]
    result = {name: (covered, count) for name, (covered, count) in crates.items()}
    totals = data["totals"]["lines"]
    result["total"] = (totals["covered"], totals["count"])
    return result


def main() -> None:
    if len(sys.argv) != 3 or sys.argv[1] not in ("dotnet", "rust"):
        sys.exit(__doc__)
    kind, path = sys.argv[1], pathlib.Path(sys.argv[2])
    measured = dotnet(path) if kind == "dotnet" else rust(path)
    minimums: dict[str, float] = json.loads((HERE / "thresholds.json").read_text())[kind]

    rows = ["| | Lines covered | Minimum | |", "|---|---:|---:|---|"]
    failures = []
    for name in sorted(set(measured) | set(minimums)):
        covered, count = measured.get(name, (0, 0))
        percent = 100.0 * covered / count if count else 0.0
        minimum = minimums.get(name)
        if minimum is None:
            status = ""
        elif name not in measured:
            status = "missing"
            failures.append(f"{name}: not in the coverage report")
        elif percent + 1e-9 < minimum:
            status = "below"
            failures.append(f"{name}: {percent:.1f}% of lines, minimum {minimum}%")
        else:
            status = "ok"
        shown_minimum = "" if minimum is None else f"{minimum}%"
        rows.append(f"| {name} | {percent:.1f}% ({covered}/{count}) | {shown_minimum} | {status} |")

    title = {"dotnet": ".NET line coverage", "rust": "Rust line coverage"}[kind]
    text = f"### {title}\n\n" + "\n".join(rows) + "\n"
    print(text)
    summary_file = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_file:
        with open(summary_file, "a", encoding="utf-8") as out:
            out.write(text + "\n")
    if failures:
        print("Coverage below the minimums in tools/coverage/thresholds.json:", file=sys.stderr)
        for failure in failures:
            print(f"  {failure}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
