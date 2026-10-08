"""Evaluate catalog and consuming-project SDK selections through MSBuild without launching apps."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import tempfile
import uuid
import xml.etree.ElementTree as ET
from pathlib import Path


def _local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def _controlled_property(name: str) -> bool:
    return (name.casefold().startswith("hexalith") and name.casefold().endswith("version")) or name.casefold() in {
        "hexalithredisimage", "hexalithredisimagetag", "hexalithredisimagedigest", "hexalithversionsloaded"}


def _run(path: Path, arguments: list[str], *, neutral: bool = False,
         configuration: str = "Debug", target_framework: str = "net10.0") -> str:
    environment = dict(os.environ)
    if neutral:
        environment = {key: value for key, value in environment.items() if not _controlled_property(key)}
    command = ["dotnet", "msbuild", str(path), "-nologo", "-p:Configuration=" + configuration,
               "-p:TargetFramework=" + target_framework, "-p:UseHexalithProjectReferences=true", *arguments]
    try:
        result = subprocess.run(command, cwd=path.parent, env=environment, capture_output=True, text=True,
                                timeout=60, check=False)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise ValueError(f"MSBuild evaluation unavailable: {path}: {error}") from error
    if result.returncode:
        raise ValueError(f"MSBuild evaluation failed: {path}: {result.stdout} {result.stderr}")
    return result.stdout


def evaluate_catalog(path: Path, configuration: str = "Debug",
                     target_framework: str = "net10.0") -> tuple[dict[str, str], dict[str, str]]:
    """Read authoritative evaluated properties/CPM items with inherited selections removed."""
    path = path.resolve()
    with tempfile.TemporaryDirectory(prefix="hexalith-evaluated-catalog-") as temporary:
        preprocessed = Path(temporary) / "catalog.xml"
        _run(path, ["-preprocess:" + str(preprocessed)], neutral=True,
             configuration=configuration, target_framework=target_framework)
        document = ET.parse(preprocessed).getroot()
    names = sorted({_local_name(item.tag) for group in document.iter()
                    if _local_name(group.tag) == "PropertyGroup" for item in group} | {"HexalithAspireAppHostSdkVersion"})
    arguments = ["-getItem:PackageVersion"]
    if names:
        arguments.append("-getProperty:" + ",".join(names))
    evaluation = json.loads(_run(path, arguments, neutral=True, configuration=configuration,
                                target_framework=target_framework))
    properties = evaluation.get("Properties", {})
    versions = {}
    identities = set()
    for item in evaluation.get("Items", {}).get("PackageVersion", []):
        package, version = item["Identity"], item.get("Version", "")
        if package.casefold() in identities:
            raise ValueError(f"Duplicate evaluated central package: {package}")
        identities.add(package.casefold())
        versions[package] = version
    return properties, versions


def has_apphost_sdk(document: ET.Element) -> bool:
    """Identify only controlled declarations; conditions are decided by MSBuild evaluation."""
    return any(reference.split("/", 1)[0].strip() == "Aspire.AppHost.Sdk"
               for reference in document.get("Sdk", "").split(";")) or any(
        (_local_name(item.tag) == "Sdk" and item.get("Name") == "Aspire.AppHost.Sdk") or
        (_local_name(item.tag) == "Import" and item.get("Sdk") == "Aspire.AppHost.Sdk")
        for item in document.iter())


def project_sdk_selections(path: Path, properties: dict[str, str] | None = None) -> list[dict[str, object]]:
    """Observe effective controlled SDK declarations, with project overrides and import conditions.

    SDK declarations become MSBuild items at their declaration points, so auditing does not
    resolve SDK packages, restore dependencies or invoke any build/application targets.
    Ordinary imports and their conditions are still evaluated by MSBuild itself.
    """
    path = path.resolve()
    document = ET.parse(path).getroot()
    if not has_apphost_sdk(document):
        return []
    # MSBuild's legacy XML namespace and modern unqualified XML have identical meaning.
    for item in document.iter():
        item.tag = _local_name(item.tag)
    root = ET.Element("Project")
    defaults = ET.SubElement(root, "PropertyGroup")
    for name, value in (properties or {}).items():
        # Only establish the SDK default. Project declarations/imports subsequently win.
        if name == "HexalithAspireAppHostSdkVersion":
            ET.SubElement(defaults, name, {"Condition": f"'$({name})' == ''"}).text = value
    for directory in [path.parent, *path.parents[1:]]:
        build_props = directory / "Directory.Build.props"
        if build_props.is_file():
            ET.SubElement(root, "Import", {"Project": str(build_props)})
            break

    observation_prefix = "_HexalithSdkObservation_" + uuid.uuid4().hex
    observation_count = 0

    def record(parent: ET.Element, version: str, condition: str = "") -> None:
        nonlocal observation_count
        observation_count += 1
        selected_version = observation_prefix + "Version" + str(observation_count)
        enabled = observation_prefix + "Enabled" + str(observation_count)
        # MSBuild evaluates properties/imports before item metadata. Capture both version
        # and activation during that first pass so later assignments cannot hide drift.
        capture = ET.SubElement(parent, "PropertyGroup", {"Condition": condition} if condition else {})
        ET.SubElement(capture, selected_version).text = version
        ET.SubElement(capture, enabled).text = "true"
        group = ET.SubElement(parent, "ItemGroup", {"Condition": f"'$({enabled})' == 'true'"})
        item = ET.SubElement(group, "_HexalithControlledSdk", {"Include": "Aspire.AppHost.Sdk"})
        ET.SubElement(item, "Version").text = f"$({selected_version})"
        ET.SubElement(item, "CatalogSelected").text = str(version == "$(HexalithAspireAppHostSdkVersion)").lower()

    root_versions = [reference.split("/", 1)[1] if "/" in reference else ""
                     for reference in document.attrib.pop("Sdk", "").split(";")
                     if reference.split("/", 1)[0].strip() == "Aspire.AppHost.Sdk"]

    def transform(parent: ET.Element) -> None:
        nonlocal observation_count
        for item in list(parent):
            name = _local_name(item.tag)
            if name == "ImportGroup" and any(child.get("Sdk") == "Aspire.AppHost.Sdk" for child in item):
                # ImportGroup accepts only Import children. Capture its activation once,
                # then flatten its children into valid siblings at the original position.
                observation_count += 1
                enabled = observation_prefix + "GroupEnabled" + str(observation_count)
                holder = ET.Element("Project")
                condition = item.get("Condition", "")
                capture = ET.SubElement(holder, "PropertyGroup", {"Condition": condition} if condition else {})
                ET.SubElement(capture, enabled).text = "true"
                for child in list(item):
                    child_condition = child.get("Condition", "")
                    group_condition = f"'$({enabled})' == 'true'"
                    child.set("Condition", f"({group_condition}) And ({child_condition})" if child_condition else group_condition)
                    holder.append(child)
                transform(holder)
                index = list(parent).index(item)
                parent.remove(item)
                for offset, sibling in enumerate(list(holder)):
                    parent.insert(index + offset, sibling)
                continue
            if name == "Sdk" or (name == "Import" and "Sdk" in item.attrib):
                identifier = item.get("Name") if name == "Sdk" else item.get("Sdk")
                index = list(parent).index(item)
                parent.remove(item)
                if identifier == "Aspire.AppHost.Sdk":
                    holder = ET.Element("Project")
                    record(holder, item.get("Version", ""), item.get("Condition", ""))
                    for offset, observation in enumerate(list(holder)):
                        parent.insert(index + offset, observation)
                continue
            transform(item)

    transform(document)
    for version in root_versions:
        record(root, version)
    root.extend(list(document))
    # Evaluate the transformed XML in memory under the original FullPath. This retains
    # MSBuildProjectName/Directory, relative imports, Exists and imported-file conditions.
    # The runner executes only this evaluation task; no consuming-project target runs.
    with tempfile.TemporaryDirectory(prefix="hexalith-sdk-evaluation-") as temporary:
        directory = Path(temporary)
        xml_path = directory / "consumer.xml"
        ET.ElementTree(root).write(xml_path, encoding="utf-8", xml_declaration=True)
        runner = ET.Element("Project")
        using = ET.SubElement(runner, "UsingTask", {
            "TaskName": "ObserveControlledSdk", "TaskFactory": "RoslynCodeTaskFactory",
            "AssemblyFile": "$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll"})
        parameters = ET.SubElement(using, "ParameterGroup")
        for name in ["ConsumerXml", "ConsumerPath", "BuildAssembly"]:
            ET.SubElement(parameters, name, {"ParameterType": "System.String", "Required": "true"})
        ET.SubElement(parameters, "Selections", {"ParameterType": "Microsoft.Build.Framework.ITaskItem[]", "Output": "true"})
        task = ET.SubElement(using, "Task")
        code = ET.SubElement(task, "Code", {"Type": "Fragment", "Language": "cs"})
        # RoslynCodeTaskFactory uses netstandard reference assemblies; reflection keeps
        # the helper compatible with the host SDK's Microsoft.Build runtime assembly.
        code.text = r'''var assembly = System.Reflection.Assembly.LoadFrom(BuildAssembly);
var collectionType = assembly.GetType("Microsoft.Build.Evaluation.ProjectCollection");
var collection = System.Activator.CreateInstance(collectionType);
try
{
    var rootType = assembly.GetType("Microsoft.Build.Construction.ProjectRootElement");
    System.Reflection.MethodInfo create = null;
    foreach (var method in rootType.GetMethods())
    {
        var parameters = method.GetParameters();
        if (method.Name == "Create" && parameters.Length == 2 && parameters[0].ParameterType.Name == "XmlReader")
        {
            create = method;
            break;
        }
    }
    object model;
    using (var reader = System.Xml.XmlReader.Create(ConsumerXml))
    {
        model = create.Invoke(null, new object[] { reader, collection });
    }
    rootType.GetProperty("FullPath").SetValue(model, ConsumerPath);
    var globals = new System.Collections.Generic.Dictionary<string, string>
    {
        { "Configuration", "Debug" },
        { "UseHexalithProjectReferences", "true" }
    };
    var projectType = assembly.GetType("Microsoft.Build.Evaluation.Project");
    var project = System.Activator.CreateInstance(projectType, new object[] { model, globals, null, collection });
    var items = (System.Collections.IEnumerable)projectType.GetMethod("GetItems").Invoke(project, new object[] { "_HexalithControlledSdk" });
    var selections = new System.Collections.Generic.List<Microsoft.Build.Framework.ITaskItem>();
    foreach (var item in items)
    {
        var itemType = item.GetType();
        var observation = new Microsoft.Build.Utilities.TaskItem((string)itemType.GetProperty("EvaluatedInclude").GetValue(item));
        foreach (var name in new string[] { "Version", "CatalogSelected" })
        {
            observation.SetMetadata(name, (string)itemType.GetMethod("GetMetadataValue").Invoke(item, new object[] { name }));
        }
        selections.Add(observation);
    }
    Selections = selections.ToArray();
}
finally
{
    ((System.IDisposable)collection).Dispose();
}'''
        target = ET.SubElement(runner, "Target", {"Name": "Observe"})
        invoke = ET.SubElement(target, "ObserveControlledSdk", {"ConsumerXml": str(xml_path), "ConsumerPath": str(path), "BuildAssembly": "$(MSBuildToolsPath)/Microsoft.Build.dll"})
        ET.SubElement(invoke, "Output", {"TaskParameter": "Selections", "ItemName": "_HexalithControlledSdk"})
        runner_path = directory / "audit.proj"
        ET.ElementTree(runner).write(runner_path, encoding="utf-8", xml_declaration=True)
        evaluation = json.loads(_run(runner_path, ["-target:Observe", "-getItem:_HexalithControlledSdk"]))
    selections = []
    seen = set()
    for item in evaluation.get("Items", {}).get("_HexalithControlledSdk", []):
        version = item.get("Version", "")
        if not version or "$" in version or "@(" in version:
            raise ValueError(f"Unresolved Aspire.AppHost.Sdk catalog field: {path}: {version or '<missing>'}")
        selected = item.get("CatalogSelected") == "true"
        identity = (version, selected)
        if identity not in seen:
            seen.add(identity)
            selections.append({"Id": "Aspire.AppHost.Sdk", "Version": version, "CatalogSelected": selected})
    return selections


def apphost_sdk_versions(path: Path, properties: dict[str, str] | None = None) -> set[str]:
    """Return consuming-project selections as release/prerelease version strings."""
    return {str(item["Version"]) for item in project_sdk_selections(path, properties)}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    selection = parser.add_mutually_exclusive_group(required=True)
    selection.add_argument("--project", type=Path)
    selection.add_argument("--catalog", type=Path)
    parser.add_argument("--catalog-sdk", default="")
    arguments = parser.parse_args()
    try:
        defaults = {"HexalithAspireAppHostSdkVersion": arguments.catalog_sdk} if arguments.catalog_sdk else {}
        if arguments.catalog:
            properties, versions = evaluate_catalog(arguments.catalog)
            print(json.dumps({"Properties": properties, "Items": {"PackageVersion": [
                {"Identity": package, "Version": version} for package, version in versions.items()]}}))
        else:
            print(json.dumps(project_sdk_selections(arguments.project, defaults)))
    except (ValueError, OSError, ET.ParseError) as error:
        parser.exit(1, str(error) + "\n")
