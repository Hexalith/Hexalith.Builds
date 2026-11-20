"""Select the newest stable EventStore release available for every catalog member."""

import argparse
from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
import re
from urllib.request import urlopen
import xml.etree.ElementTree as ET


VERSION = re.compile(r"[0-9]+(?:\.[0-9]+){2,3}")
CATALOG = Path("Props/Directory.Packages.props")
PROPERTY = "HexalithEventStoreVersion"


def stable_versions(package):
    url = f"https://api.nuget.org/v3-flatcontainer/{package.lower()}/index.json"
    with urlopen(url, timeout=30) as response:
        return {version for version in json.load(response)["versions"] if VERSION.fullmatch(version)}


def latest_release(content, lookup=stable_versions):
    root = ET.fromstring(content)
    declarations = root.findall(f"PropertyGroup/{PROPERTY}")
    if len(declarations) != 1 or not VERSION.fullmatch(declarations[0].text or ""):
        raise ValueError("Expected a single stable EventStore version declaration")
    current = declarations[0].text
    packages = []
    for item in root.findall("ItemGroup/PackageVersion"):
        package = item.get("Include", item.get("Update", ""))
        if package.lower().startswith("hexalith.eventstore."):
            if item.get("Version") != f"$({PROPERTY})" or not re.fullmatch(r"[A-Za-z0-9.]+", package):
                raise ValueError(f"Unexpected EventStore declaration for {package}")
            packages.append(package)
    if not packages:
        raise ValueError("No EventStore packages in the catalog")
    with ThreadPoolExecutor(max_workers=4) as pool:
        common = set.intersection(*pool.map(lookup, packages))
    if current not in common:
        raise ValueError(f"Current EventStore release {current} is missing from one or more feeds")
    return current, max(common, key=lambda version: tuple(map(int, version.split("."))))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--write", action="store_true")
    args = parser.parse_args()
    content = CATALOG.read_bytes()
    current, latest = latest_release(content)
    if args.write and latest != current:
        suffix = f"</{PROPERTY}>".encode()
        old = b">" + current.encode() + suffix
        if content.count(old) != 1:
            raise ValueError("Cannot locate a unique literal version in the catalog")
        CATALOG.write_bytes(content.replace(old, b">" + latest.encode() + suffix))
    print(latest)


if __name__ == "__main__":
    main()
