"""Read-only local orchestration of the shared CI stages 1–3.

The caller supplies a committed, clean module and initialized direct dependencies.
This command never fetches into, initializes or retargets any caller checkout.
"""

from __future__ import annotations

import argparse
from pathlib import Path
import re
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import ci_pipeline
from ci_config import ConfigError, load_config
import expected_state as state
import guard


BUILDS_PATH = "references/Hexalith.Builds"
GITHUB_ORIGIN = re.compile(
    r"(?:https://github\.com/|git@github\.com:|ssh://git@github\.com/)"
    r"([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+?)(?:\.git)?/?\Z")


def git_at(path: Path) -> state.Git:
    """Disable executable fsmonitor helpers and optional index writes in every call."""
    git = state.Git(path)
    git.environment.update(GIT_OPTIONAL_LOCKS="0", GIT_CONFIG_COUNT="2",
                           GIT_CONFIG_KEY_0="core.fsmonitor", GIT_CONFIG_VALUE_0="false",
                           GIT_CONFIG_KEY_1="core.untrackedCache", GIT_CONFIG_VALUE_1="false")
    return git


def commit(git: state.Git, ref: str, label: str) -> str:
    """Resolve a locally available commit to its full canonical identity."""
    value = git.run("rev-parse", "--verify", "--end-of-options", ref + "^{commit}", check=False).decode().strip()
    if not state.SHA.fullmatch(value):
        raise ci_pipeline.PipelineError(label + ": required local commit is unavailable")
    return value


def checkout(git: state.Git, snapshot, path: str) -> state.Git:
    """Require an exact HEAD gitlink and a separate checkout at that commit."""
    item = snapshot.items.get(path)
    if item is None or item.mode != "160000" or not state.SHA.fullmatch(item.oid):
        raise state.ExpectedStateError(path + ": HEAD must record a direct 160000 Git gitlink")
    directory = ci_pipeline.contained_path(git.root, path, kind="submodule")
    if not directory.is_dir():
        raise state.ExpectedStateError(path + ": initialize the required direct submodule at its committed pin")
    child = git_at(directory)
    top = child.run("rev-parse", "--show-toplevel", check=False).decode().removesuffix("\n")
    if not top or Path(top).resolve() != directory:
        raise state.ExpectedStateError(path + ": initialize the required separate submodule checkout at its committed pin")
    head = commit(child, "HEAD", path + " checkout HEAD")
    if head != item.oid:
        raise state.ExpectedStateError(path + ": checkout HEAD " + head + " differs from committed gitlink " + item.oid)
    return child


def require_clean(git: state.Git, label: str) -> None:
    """Refuse staged, dirty and untracked inputs without refreshing the index."""
    # Status may execute clean/process filters while comparing worktree bytes.
    # Includes and later overrides have the same precedence as the comparisons.
    filters = {}
    records = git.run("config", "--includes", "--null", "--get-regexp",
                      r"^filter\..*\.(clean|process)$", check=False)
    for record in records.split(b"\0"):
        if record:
            name, _, value = record.partition(b"\n")
            filters[name] = value
    if any(filters.values()):
        raise ci_pipeline.PipelineError(label + ": executable Git clean/process filters are unsupported; "
                                        "disable them before checking")
    # Ignore submodule working trees here: required direct checkouts are checked
    # separately, and unselected dependencies need not be initialized or clean.
    staged = git.run("diff", "--cached", "--name-only", "-z", "--no-ext-diff", "--no-textconv",
                     "--ignore-submodules=none", "HEAD", "--")
    # Without rename records every porcelain entry is "XY path".
    dirty = git.run("status", "--porcelain=v1", "-z", "--no-renames", "--untracked-files=all",
                    "--ignore-submodules=all")
    # Git status intentionally hides these entries. Missing sparse sources or
    # assume-unchanged files cannot establish that disk content matches HEAD.
    hidden = [entry[2:] for entry in git.run("ls-files", "-v", "-z").split(b"\0")
              if entry and (entry[:1] == b"S" or entry[:1].islower())]
    paths = [path.decode("utf-8", "backslashreplace") for path in dict.fromkeys(
        (*staged.split(b"\0"), *(entry[3:] for entry in dirty.split(b"\0")), *hidden)) if path]
    if paths:
        listed = ", ".join(paths[:10]) + (", ..." if len(paths) > 10 else "")
        raise ci_pipeline.PipelineError(label + ": commit all staged, modified and untracked inputs before checking; "
                                        "populate sparse paths and clear assume-unchanged/skip-worktree flags: "
                                        + listed)


def repository_identity(git: state.Git) -> str:
    """Derive trusted guard context only from a standard GitHub origin URL."""
    origins = git.run("config", "--local", "--get-all", "remote.origin.url", check=False).decode().splitlines()
    match = GITHUB_ORIGIN.fullmatch(origins[0]) if len(origins) == 1 else None
    if match is None:
        raise ci_pipeline.PipelineError("origin must have one standard GitHub HTTPS or SSH repository URL")
    repository = match[1]
    if any(part in (".", "..") for part in repository.split("/")):
        raise ci_pipeline.PipelineError("origin has an invalid GitHub repository identity")
    state.validate_context(repository)
    return repository


def require_declarations(git: state.Git, head: str, paths: tuple[str, ...]) -> None:
    """Read direct declarations from committed .gitmodules, never initialize them."""
    records = git.run("config", "--no-includes", "--null", "--blob", head + ":.gitmodules",
                      "--get-regexp", r"^submodule\..*\.path$", check=False)
    declared = [record.partition(b"\n")[2].decode() for record in records.split(b"\0") if record]
    if len(declared) != len(set(declared)):
        raise ci_pipeline.PipelineError("duplicate direct submodule paths in committed .gitmodules")
    for path in paths:
        if path not in declared:
            raise ci_pipeline.PipelineError(path + ": required direct submodule is not declared in committed .gitmodules")


def revalidate(git: state.Git, head: str, snapshot, paths: tuple[str, ...], tag: str) -> None:
    """Refuse changes made while the guard or product tools were running."""
    if commit(git, "HEAD", "module HEAD") != head:
        raise ci_pipeline.PipelineError("module HEAD changed during the local check; rerun on the intended committed inputs")
    require_clean(git, "module")
    for path in paths:
        child = checkout(git, snapshot, path)
        require_clean(child, path)
        if path == BUILDS_PATH and commit(child, "refs/tags/" + tag, "Builds tag " + tag) != snapshot.items[path].oid:
            raise state.ExpectedStateError("Builds tag " + tag + " changed during the local check; restore the intended pin and rerun")


def run(workspace: Path, *, transport=None, tool_factory=guard.Tools) -> None:
    """Freeze module identities, validate inputs, then reuse the shared stages."""
    initial = git_at(workspace)
    top = initial.run("rev-parse", "--show-toplevel", check=False).decode().removesuffix("\n")
    if not top:
        raise ci_pipeline.PipelineError("run the command inside the module's Git work tree, "
                                        "owned by the current user")
    git = git_at(Path(top))
    head = commit(git, "HEAD", "module HEAD")
    snapshot = state.tree_snapshot(git, head)
    # Builds has no Builds gitlink; recognize its origin before module pin checks.
    repository = repository_identity(git)
    # GitHub repository names are case-insensitive.
    if repository.casefold() == ci_pipeline.BUILDS_REPOSITORY.casefold():
        raise ci_pipeline.PipelineError("Builds itself is checked by its own CI; the local command requires a module origin")
    # Identity failures precede JSON parsing and SDK metadata/commands.
    builds = checkout(git, snapshot, BUILDS_PATH)
    require_clean(git, "module")
    require_clean(builds, BUILDS_PATH)
    document = load_config(git.root / ".github/hexalith-ci.json", repository=repository)
    tag = document["buildsTag"]
    tagged = commit(builds, "refs/tags/" + tag, "Builds tag " + tag + "; fetch this exact tag before checking")
    if tagged != snapshot.items[BUILDS_PATH].oid:
        raise state.ExpectedStateError("Builds tag " + tag + " resolves to " + tagged
                                       + ", different from the committed gitlink " + snapshot.items[BUILDS_PATH].oid)
    paths = tuple(dict.fromkeys((BUILDS_PATH, *document["submodules"])))
    require_declarations(git, head, paths)
    for path in paths:
        if path != BUILDS_PATH:
            require_clean(checkout(git, snapshot, path), path)
    base = commit(git, "refs/remotes/origin/main", "origin/main; fetch the base before checking")
    if git.run("rev-parse", "--is-shallow-repository").strip() != b"false":
        raise ci_pipeline.PipelineError("module history is shallow; fetch --unshallow origin with complete history "
                                        "before checking origin/main..HEAD")
    guard.run(git.root, repository=repository, ref=head, commit_range=(base, head),
              emit=lambda values: None, transport=transport, tool_factory=tool_factory)
    revalidate(git, head, snapshot, paths, tag)
    prepared = ci_pipeline.prepare_config(git.root, repository=repository)
    ci_pipeline.restore_and_build(prepared)
    revalidate(git, head, snapshot, paths, tag)
    ci_pipeline.run_unit_tests(prepared, repository=repository)
    revalidate(git, head, snapshot, paths, tag)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.parse_args()
    try:
        if sys.version_info < (3, 12):
            raise ci_pipeline.PipelineError("Python 3.12 or later is required")
        run(Path.cwd())
        return 0
    except (ConfigError, state.ExpectedStateError, state.perimeter.PerimeterError, OSError, UnicodeError) as error:
        print(state.escaped(error) if isinstance(error, (OSError, UnicodeError)) else str(error), file=sys.stderr)
        return getattr(error, "returncode", 1)


if __name__ == "__main__":
    sys.dont_write_bytecode = True
    raise SystemExit(main())
