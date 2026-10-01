#!/usr/bin/env python3
"""Fails when a NuGet package the solution uses, directly or transitively, has a known
vulnerability (`dotnet list package --vulnerable --include-transitive`, which only prints).
Run after `dotnet restore`; CI's dependency audit runs it."""
import json
import subprocess
import sys

output = subprocess.run(
    ["dotnet", "list", "package", "--vulnerable", "--include-transitive", "--format", "json"],
    check=True,
    capture_output=True,
    text=True,
).stdout
report = json.loads(output)

findings = []
for project in report.get("projects", []):
    for framework in project.get("frameworks", []):
        for kind in ("topLevelPackages", "transitivePackages"):
            for package in framework.get(kind, []):
                for vulnerability in package.get("vulnerabilities", []):
                    findings.append(
                        f"{project.get('path', '?')}: {package.get('id')} {package.get('resolvedVersion')} "
                        f"({vulnerability.get('severity')}: {vulnerability.get('advisoryurl')})"
                    )

for problem in report.get("problems", []):
    print(f"warning: {problem.get('text', problem)}", file=sys.stderr)

if findings:
    print("Vulnerable NuGet packages:", file=sys.stderr)
    for finding in sorted(set(findings)):
        print(f"  {finding}", file=sys.stderr)
    sys.exit(1)
print(f"No known vulnerabilities in the NuGet packages of {len(report.get('projects', []))} projects.")
