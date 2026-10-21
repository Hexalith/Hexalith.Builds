"""Read-only observation of the existing P1R campaign; never drives qualification."""
from pathlib import Path
from datetime import datetime, timezone
import json
import ast

ROOT = Path('/home/administrator/projects/hexalith/p0-approved-qualification-20261020/references/Hexalith.EventStore/.p0-qualification/p1r-31260-execution-attempt2')
OUTPUT = Path(__file__).with_name("p1r-live-progress.json")
previous = json.loads(OUTPUT.read_text()) if OUTPUT.exists() else {}
seen = {row["path"] for row in previous.get("completedReceipts", [])}
rows = []
for path in sorted((ROOT / "receipts").glob("*.json")):
    data = json.loads(path.read_text())
    row = {"path": str(path), "lane": data.get("lane", data.get("scenario")),
           "execution": data.get("execution"), "assertions": data.get("assertions")}
    rows.append(row)
    if str(path) not in seen:
        print("new receipt", json.dumps(row))
lanes = [row for row in rows if row["lane"] is not None]
summary = {key: sum((row["assertions"] or {}).get(key, 0) for row in lanes)
           for key in ("attempted", "passed", "failed")}
print("completed lanes", len(lanes), "assertions", summary)
source = ROOT.parents[1] / "tools/p1r_qualification.py"
constants = {node.targets[0].id: ast.literal_eval(node.value)
             for node in ast.parse(source.read_text()).body
             if isinstance(node, ast.Assign) and isinstance(node.targets[0], ast.Name)
             and node.targets[0].id in {"SCENARIOS", "ADDITIONS"}}
finished = {row["lane"] for row in lanes}
next_lane = next((name for name in constants["SCENARIOS"] + constants["ADDITIONS"] if name not in finished), None)
print("next unfinished lane", next_lane)
commands = ROOT / "commands.json"
if commands.exists():
    values = json.loads(commands.read_text())
    print("completed commands", len(values))
execution = ROOT / "execution.json"
if execution.exists():
    print("executor errors", json.loads(execution.read_text()).get("errors"))
OUTPUT.with_suffix(".pending").write_text(json.dumps({"recordedUtc": datetime.now(timezone.utc).isoformat(),
    "executionRoot": str(ROOT), "scope": "incomplete campaign observation; not acceptance",
    "complete": False, "qualified": False, "usable": False,
    "completedReceipts": rows}, indent=2) + "\n")

OUTPUT.with_suffix(".pending").replace(OUTPUT)
