#!/usr/bin/env python3
"""Check processed release CodeQL alerts using trusted stdlib API reads.

The workflow calls this only after analyze has successfully waited for SARIF
processing. This gate reads the most recent instance on the default branch;
it never searches historical instances or other analysis configurations.
"""

from __future__ import annotations

import os
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from release_state import Api, ReleaseError


PREFIX = "hexalith-release/"
STATES = frozenset(("open", "closed", "dismissed", "fixed"))
SEVERITIES = frozenset(("critical", "high", "medium", "low"))


def blocking_alert(alert: object) -> int | None:
    """Return the alert number when its current release instance blocks.

Nonsecurity rules may omit the nullable security severity. Older analyses
may omit a category; neither identifies a release security finding. Invalid
types or missing required API data fail instead of masquerading as no alerts.
"""
    if not isinstance(alert, dict):
        raise ReleaseError("code-scanning alerts endpoint returned a non-object alert")
    number = alert.get("number")
    state = alert.get("state")
    rule = alert.get("rule")
    instance = alert.get("most_recent_instance")
    if type(number) is not int or number < 1:
        raise ReleaseError("code-scanning alert has an invalid number")
    if not isinstance(state, str) or state not in STATES:
        raise ReleaseError(f"code-scanning alert #{number} has an invalid state")
    if not isinstance(rule, dict) or not isinstance(instance, dict):
        raise ReleaseError(f"code-scanning alert #{number} has incomplete rule or most-recent-instance data")
    severity = rule.get("security_severity_level")
    category = instance.get("category")
    if severity is not None and (not isinstance(severity, str) or severity not in SEVERITIES):
        raise ReleaseError(f"code-scanning alert #{number} has invalid security severity")
    if category is not None and not isinstance(category, str):
        raise ReleaseError(f"code-scanning alert #{number} has an invalid most-recent-instance category")
    if state == "open" and severity in ("high", "critical") and category is not None and category.startswith(PREFIX):
        return number
    return None


def check_alerts(api: Api) -> int:
    """Read every alert page and reject qualifying current release findings."""
    blocked = []
    checked = 0
    endpoint = f"/repos/{api.repository}/code-scanning/alerts?state=open&per_page=100"
    for alert in api.pages(endpoint):
        checked += 1
        number = blocking_alert(alert)
        if number is not None:
            blocked.append(number)
    if blocked:
        numbers = ", ".join(f"#{number}" for number in sorted(set(blocked)))
        raise ReleaseError(f"release CodeQL security gate blocked by open high/critical alerts: {numbers}")
    return checked


def main() -> int:
    try:
        if sys.version_info < (3, 12):
            raise ReleaseError("Python 3.12 or later is required")
        api = Api(os.environ.get("GITHUB_REPOSITORY", ""), os.environ.get("GITHUB_TOKEN", ""),
                  base_url=os.environ.get("GITHUB_API_URL", "https://api.github.com"))
        checked = check_alerts(api)
        print(f"release CodeQL security gate passed; checked {checked} alerts after SARIF processing")
        return 0
    except (ReleaseError, OSError) as error:
        print(error, file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
