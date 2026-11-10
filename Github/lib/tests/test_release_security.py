"""Execute release-security against paginated local API and adapter controls."""

from __future__ import annotations

from contextlib import contextmanager
import http.server
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import unittest

import yaml

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import release_security as security
from release_state import Api, ReleaseError


ROOT = Path(__file__).resolve().parents[3]
REPOSITORY = "Example/ReleaseFixture"
ENDPOINT = f"/repos/{REPOSITORY}/code-scanning/alerts?state=open&per_page=100"
TOKEN = "synthetic-release-security-token"


def alert(number=1, *, state="open", severity="high", category="hexalith-release/csharp"):
    return {"number": number, "state": state,
            "rule": {"severity": "error", "security_severity_level": severity},
            "most_recent_instance": {"category": category}}


@contextmanager
def api_control(responses):
    """Record real HTTP requests; response callbacks can provide live Link URLs."""
    requests = []

    class Handler(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            requests.append((self.path, self.headers.get("Authorization")))
            response = responses.get(self.path, (404, {}, b"not found"))
            if callable(response):
                response = response(server.server_port)
            status, headers, body = response
            self.send_response(status)
            for name, value in headers.items():
                self.send_header(name, value)
            self.end_headers()
            self.wfile.write(body if isinstance(body, bytes) else json.dumps(body).encode())

        def log_message(self, *_args):
            pass

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    worker = threading.Thread(target=server.serve_forever, kwargs={"poll_interval": 0.01}, daemon=True)
    worker.start()
    try:
        yield Api(REPOSITORY, TOKEN, base_url=f"http://127.0.0.1:{server.server_port}"), requests, server.server_port
    finally:
        server.shutdown()
        server.server_close()
        worker.join()


class ReleaseSecurityTests(unittest.TestCase):
    def test_only_current_open_high_and_critical_release_categories_block(self):
        for severity in ("high", "critical"):
            with self.subTest(severity=severity):
                self.assertEqual(4, security.blocking_alert(alert(4, severity=severity)))
        for value in [alert(state="closed"), alert(state="dismissed"), alert(state="fixed"),
                      alert(severity="medium"), alert(severity="low"), alert(severity=None),
                      alert(category="hexalith-release-old/csharp"), alert(category="legacy-codeql"),
                      alert(category=""), alert(category=None)]:
            with self.subTest(alert=value):
                self.assertIsNone(security.blocking_alert(value))
        nonsecurity = alert()
        del nonsecurity["rule"]["security_severity_level"]
        del nonsecurity["most_recent_instance"]["category"]
        self.assertIsNone(security.blocking_alert(nonsecurity))

    def test_historical_release_instances_and_plain_severity_do_not_block(self):
        value = alert(severity=None, category="other-analysis")
        value["instances"] = [{"category": "hexalith-release/csharp", "state": "open"}]
        value["rule"]["severity"] = "error"
        self.assertIsNone(security.blocking_alert(value))
        value["rule"]["security_severity_level"] = "critical"
        self.assertIsNone(security.blocking_alert(value))

    def test_malformed_alert_data_is_a_failure(self):
        mutations = [{"number": True}, {"number": 0}, {"number": "1"}, {"state": "unknown"},
                     {"state": None}, {"rule": None}, {"most_recent_instance": None},
                     {"rule": {"security_severity_level": "error"}},
                     {"rule": {"security_severity_level": ["high"]}},
                     {"most_recent_instance": {"category": ["hexalith-release/csharp"]}}]
        for mutation in mutations:
            with self.subTest(mutation=mutation), self.assertRaises(ReleaseError):
                security.blocking_alert({**alert(), **mutation})
        for value in (None, [], "alert", {}, {"number": 1}):
            with self.subTest(value=value), self.assertRaises(ReleaseError):
                security.blocking_alert(value)

    def test_real_pagination_finds_later_blockers_and_names_each_number(self):
        next_path = f"/repos/{REPOSITORY}/code-scanning/alerts?state=open&per_page=100&page=2"
        responses = {
            ENDPOINT: lambda port: (200, {"Link": f'<http://127.0.0.1:{port}{next_path}>; rel="next"'},
                                    [alert(1, severity="low"), alert(2, category="legacy")]),
            next_path: (200, {}, [alert(9, severity="critical"), alert(3)]),
        }
        with api_control(responses) as (api, requests, _port):
            with self.assertRaisesRegex(ReleaseError, r"#3, #9"):
                security.check_alerts(api)
        self.assertEqual([ENDPOINT, next_path], [path for path, _auth in requests])
        self.assertEqual(["Bearer " + TOKEN] * 2, [auth for _path, auth in requests])

    def test_numeric_repository_links_are_verified_and_rewritten_before_collection_reads(self):
        metadata = f"/repos/{REPOSITORY}"
        page_two = f"/repos/{REPOSITORY}/code-scanning/alerts?page=2&per_page=100&state=open"
        page_three = f"/repos/{REPOSITORY}/code-scanning/alerts?page=3&per_page=100&state=open"
        responses = {
            ENDPOINT: lambda port: (200, {"Link": f'<http://127.0.0.1:{port}/repositories/123/code-scanning/alerts?page=2&per_page=100&state=open>; rel="next"'},
                                    [alert(1, severity="low")]),
            metadata: (200, {}, {"id": 123, "full_name": REPOSITORY.lower()}),
            page_two: lambda port: (200, {"Link": f'<http://127.0.0.1:{port}/repositories/123/code-scanning/alerts?page=3&per_page=100&state=open>; rel="next"'},
                                    [alert(2, severity="medium")]),
            page_three: (200, {}, [alert(9, severity="critical")]),
        }
        with api_control(responses) as (api, requests, _port):
            with self.assertRaisesRegex(ReleaseError, "#9"):
                security.check_alerts(api)
        self.assertEqual([ENDPOINT, metadata, page_two, page_three], [path for path, _auth in requests])
        self.assertEqual(["Bearer " + TOKEN] * 4, [auth for _path, auth in requests])

    def test_other_numeric_repository_ids_never_receive_authenticated_requests(self):
        metadata = f"/repos/{REPOSITORY}"
        destination = "/repositories/456/code-scanning/alerts?page=2"
        responses = {
            ENDPOINT: lambda port: (200, {"Link": f'<http://127.0.0.1:{port}{destination}>; rel="next"'}, []),
            metadata: (200, {}, {"id": 123, "full_name": REPOSITORY}),
            destination: (200, {}, [alert(9)]),
        }
        with api_control(responses) as (api, requests, _port):
            with self.assertRaisesRegex(ReleaseError, "repository ID mismatch"):
                security.check_alerts(api)
        self.assertEqual([(ENDPOINT, "Bearer " + TOKEN), (metadata, "Bearer " + TOKEN)], requests)

    def test_numeric_links_require_matching_valid_repository_metadata(self):
        metadata_path = f"/repos/{REPOSITORY}"
        for metadata in ({"id": 123, "full_name": "Other/Repository"},
                         {"id": True, "full_name": REPOSITORY}, {"id": 0, "full_name": REPOSITORY},
                         {"id": "123", "full_name": REPOSITORY}, {"id": 123}, [], None):
            with self.subTest(metadata=metadata):
                responses = {
                    ENDPOINT: lambda port: (200, {"Link": f'<http://127.0.0.1:{port}/repositories/123/code-scanning/alerts?page=2>; rel="next"'}, []),
                    metadata_path: (200, {}, metadata),
                }
                with api_control(responses) as (api, requests, _port):
                    with self.assertRaisesRegex(ReleaseError, "metadata identity mismatch"):
                        security.check_alerts(api)
                self.assertEqual([(ENDPOINT, "Bearer " + TOKEN), (metadata_path, "Bearer " + TOKEN)], requests)

    def test_numeric_links_preserve_configured_api_base_path_and_deadline(self):
        prefix = "/api/v3"
        named = prefix + f"/repos/{REPOSITORY}/code-scanning/alerts?page=2"
        metadata = prefix + f"/repos/{REPOSITORY}"
        responses = {
            prefix + ENDPOINT: lambda port: (200, {"Link": f'<http://127.0.0.1:{port}{prefix}/repositories/123/code-scanning/alerts?page=2>; rel="next"'}, []),
            metadata: (200, {}, {"id": 123, "full_name": REPOSITORY}),
            named: (200, {}, []),
        }
        with api_control(responses) as (_api, requests, port):
            api = Api(REPOSITORY, TOKEN, base_url=f"http://127.0.0.1:{port}{prefix}", clock=lambda: 5)
            self.assertEqual([], list(api.pages(ENDPOINT, deadline=10)))
            with self.assertRaisesRegex(ReleaseError, "credential boundary"):
                api.get(f"http://127.0.0.1:{port}/repositories/123/code-scanning/alerts?page=3")
        self.assertEqual([prefix + ENDPOINT, metadata, named], [path for path, _auth in requests])

    def test_every_page_must_be_available_and_well_formed(self):
        next_path = f"/repos/{REPOSITORY}/code-scanning/alerts?page=2"
        for status, body in ((403, b"forbidden"), (404, b"not enabled"), (500, b"unavailable"),
                             (200, b"invalid json"), (200, {"unexpected": []}), (200, [None])):
            with self.subTest(status=status, body=body):
                responses = {
                    ENDPOINT: lambda port: (200, {"Link": f'<http://127.0.0.1:{port}{next_path}>; rel="next"'},
                                            [alert(severity="low")]),
                    next_path: (status, {}, body),
                }
                with api_control(responses) as (api, requests, _port), self.assertRaises(ReleaseError):
                    security.check_alerts(api)
                self.assertEqual(2, len(requests))

    def test_redirects_never_forward_authenticated_requests(self):
        for status in (301, 302, 303, 307, 308):
            with self.subTest(status=status):
                responses = {
                    ENDPOINT: lambda port: (status, {"Location": f"http://127.0.0.1:{port}/capture"}, b""),
                    "/capture": (200, {}, []),
                }
                with api_control(responses) as (api, requests, _port), self.assertRaises(ReleaseError):
                    security.check_alerts(api)
                self.assertEqual([(ENDPOINT, "Bearer " + TOKEN)], requests)

    def test_pagination_cannot_leave_repository_or_api_origin(self):
        for destination in ("https://example.invalid/capture", "/repos/Other/Repository/code-scanning/alerts?page=2",
                            "/capture"):
            with self.subTest(destination=destination):
                responses = {
                    ENDPOINT: lambda port: (200, {"Link": f'<{destination if destination.startswith("https:") else "http://127.0.0.1:" + str(port) + destination}>; rel="next"'}, []),
                }
                with api_control(responses) as (api, requests, _port), self.assertRaises(ReleaseError):
                    security.check_alerts(api)
                self.assertEqual([(ENDPOINT, "Bearer " + TOKEN)], requests)

    def adapter(self, port, workspace):
        directory = ROOT / "Github/release-security"
        action = yaml.safe_load((directory / "action.yml").read_text())
        environment = dict(os.environ, GITHUB_REPOSITORY=REPOSITORY, GITHUB_TOKEN=TOKEN,
                           GITHUB_API_URL=f"http://127.0.0.1:{port}", GITHUB_ACTION_PATH=str(directory))
        result = None
        for step in action["runs"]["steps"]:
            result = subprocess.run([step["shell"], "-eo", "pipefail", "-c", step["run"]],
                                    cwd=workspace, env=environment, text=True, capture_output=True)
            if result.returncode:
                break
        return result

    def test_executed_adapter_reports_clean_blocked_and_transport_failures(self):
        for response, expected, message in [((200, {}, []), 0, "checked 0 alerts after SARIF processing"),
                                           ((200, {}, [alert(7)]), 1, "#7"),
                                           ((503, {}, b"service unavailable"), 1, "503")]:
            with self.subTest(response=response), tempfile.TemporaryDirectory() as workspace:
                # A same-named module file must never be imported from the checkout.
                poison = Path(workspace) / "release_state.py"
                poison.write_text("raise RuntimeError('module code was executed')")
                with api_control({ENDPOINT: response}) as (_api, requests, port):
                    result = self.adapter(port, workspace)
                self.assertEqual(expected, result.returncode, result.stdout + result.stderr)
                self.assertIn(message, result.stdout + result.stderr)
                self.assertNotIn(TOKEN, result.stdout + result.stderr)
                self.assertEqual([(ENDPOINT, "Bearer " + TOKEN)], requests)


if __name__ == "__main__":
    unittest.main()
