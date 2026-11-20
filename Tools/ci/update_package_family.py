"""Propose complete stable releases for the Hexalith families in the catalog audit."""

import argparse
from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
import re
from urllib.request import urlopen
import xml.etree.ElementTree as ET


VERSION = re.compile(r"[0-9]+(?:\.[0-9]+){2,3}")
CATALOG = Path("Props/Directory.Packages.props")
AUDIT = Path("Tools/package-version-audit.json")


def version_key(version):
    parts = list(map(int, version.split(".")))
    return tuple(parts + [0] * (4 - len(parts)))


def stable_versions(package):
    if not re.fullmatch(r"Hexalith\.[A-Za-z0-9.]+", package, re.I):
        raise ValueError(f"Unexpected package ID {package}")
    url = f"https://api.nuget.org/v3-flatcontainer/{package.lower()}/index.json"
    with urlopen(url, timeout=30) as response:
        return {version for version in json.load(response)["versions"] if VERSION.fullmatch(version)}


def replace_version(content, pattern, old, new):
    pattern = pattern.encode() + re.escape(old.encode()) + rb'(?=["<])'
    updated, count = re.subn(pattern, lambda match: match[1] + new.encode(), content)
    if count != 1:
        raise ValueError("Expected one catalog declaration per selected package or shared property")
    return updated


def newest_complete_release(rows, published):
    if any(row["selectedVersion"] not in versions for row, versions in zip(rows, published, strict=True)):
        raise ValueError("A selected family member is missing from NuGet")
    common = {version for version in set.intersection(*published) if VERSION.fullmatch(version)}
    if not common:
        return None
    latest = max(common, key=version_key)
    current = max((row["selectedVersion"] for row in rows), key=version_key)
    return latest if version_key(latest) > version_key(current) else None


def update_family(content, rows, latest):
    root = ET.fromstring(content)
    packages = {row["id"]: row["selectedVersion"] for row in rows}
    owners = {}
    found = set()
    for item in root.findall("ItemGroup/PackageVersion"):
        package = item.get("Include")
        value = item.get("Version", "")
        if package not in packages:
            continue
        if package in found:
            raise ValueError(f"Multiple catalog declarations for {package}")
        found.add(package)
        match = re.fullmatch(r"\$\(([A-Za-z0-9_]+)\)", value)
        if match:
            prop = match[1]
            if (len(root.findall("PropertyGroup/" + prop)) != 1 or
                    root.findtext("PropertyGroup/" + prop) != packages[package]):
                raise ValueError(f"Expected a single audited value for {prop}")
            if any(x.get("Version") == value and x.get("Include") not in packages
                   for x in root.findall("ItemGroup/PackageVersion")):
                raise ValueError(f"{prop} is shared across audit families")
            owners[rf'(<{prop}(?:\s[^>]*)?>)'] = packages[package]
        elif value == packages[package]:
            owners[rf'(<PackageVersion\s+Include="{re.escape(package)}"\s+Version=")'] = value
        else:
            raise ValueError(f"Unexpected catalog version for {package}")
    if found != set(packages):
        raise ValueError("Family audit and current catalog membership differ")
    for pattern, old in owners.items():
        content = replace_version(content, pattern, old, latest)
    return content


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", nargs=2, metavar=("FAMILY", "VERSION"))
    args = parser.parse_args()
    families = {}
    for row in json.loads(AUDIT.read_bytes())["packages"]:
        if row["id"].startswith("Hexalith."):
            family = row["family"]
            if not re.fullmatch(r"hexalith-[a-z0-9]+", family):
                raise ValueError(f"Invalid family name {family}")
            families.setdefault(family, []).append(row)
    content = CATALOG.read_bytes()
    if args.apply:
        family, latest = args.apply
        if not VERSION.fullmatch(latest):
            raise ValueError("Expected a stable release")
        CATALOG.write_bytes(update_family(content, families[family], latest))
        return
    changes = []
    with ThreadPoolExecutor(max_workers=4) as pool:
        for family, rows in families.items():
            published = list(pool.map(stable_versions, [row["id"] for row in rows]))
            latest = newest_complete_release(rows, published)
            if latest:
                update_family(content, rows, latest)
                changes.append({"family": family, "version": latest})
    print(json.dumps(changes, separators=(",", ":")))


if __name__ == "__main__":
    main()
