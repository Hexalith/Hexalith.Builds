"""Exercise the shared logging generator selection through real MSBuild evaluation."""

import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from xml.sax.saxutils import escape


class LoggingGeneratorSelectionTests(unittest.TestCase):
    def selected_analyzers(self, disable, replacement):
        catalog = Path(__file__).resolve().parents[1] / "Props/Directory.Packages.props"
        provider = '<Analyzer Include="Microsoft.Gen.Logging.dll" />' if replacement else ""
        with tempfile.TemporaryDirectory(prefix="hexalith-logging-generators-") as scratch:
            project = Path(scratch) / "selection.proj"
            project.write_text(f"""<Project>
  <Import Project="{escape(str(catalog))}" />
  <PropertyGroup>
    <DisableMicrosoftExtensionsLoggingSourceGenerator>{disable}</DisableMicrosoftExtensionsLoggingSourceGenerator>
  </PropertyGroup>
  <ItemGroup>
    {provider}
    <Analyzer Include="framework/Microsoft.Extensions.Logging.Generators.dll" AssemblyName="Microsoft.Extensions.Logging.Generators" NuGetPackageId="Microsoft.AspNetCore.App.Ref" />
    <Analyzer Include="package/Microsoft.Extensions.Logging.Generators.dll" NuGetPackageId="Microsoft.Extensions.Logging.Abstractions" />
    <Analyzer Include="Unrelated.Analyzer.dll" NuGetPackageId="Microsoft.Extensions.Logging.Abstractions" />
  </ItemGroup>
  <Target Name="ResolveReferences" />
</Project>
""")
            result = subprocess.run(
                ["dotnet", "msbuild", str(project), "-nologo", "-noAutoResponse",
                 "-t:ResolveReferences", "-getItem:Analyzer"],
                cwd=catalog.parents[1], capture_output=True, text=True, timeout=60,
                env={**os.environ, "DOTNET_CLI_USE_MSBUILD_SERVER": "0"},
            )
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            return {entry["Identity"] for entry in json.loads(result.stdout)["Items"]["Analyzer"]}

    def test_replacement_keeps_telemetry_and_unrelated_analyzers(self):
        self.assertEqual(self.selected_analyzers("true", True),
                         {"Microsoft.Gen.Logging.dll", "Unrelated.Analyzer.dll"})

    def test_without_provider_opt_out_both_generators_remain(self):
        self.assertEqual(len(self.selected_analyzers("false", True)), 4)

    def test_without_replacement_framework_generator_remains(self):
        self.assertEqual(self.selected_analyzers("true", False), {
            "framework/Microsoft.Extensions.Logging.Generators.dll",
            "package/Microsoft.Extensions.Logging.Generators.dll", "Unrelated.Analyzer.dll",
        })


if __name__ == "__main__":
    unittest.main()
