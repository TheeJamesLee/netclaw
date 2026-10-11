#!/usr/bin/env python3
"""Coarse behavioral compliance scorer for Netclaw."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import datetime as dt
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid


TOOL_CALL_RE = re.compile(r"TOOL_CALL: (?P<name>\S+) call_id=(?P<id>\S+) args=(?P<args>.*)$")
TOOL_RESULT_RE = re.compile(r"TOOL_RESULT: (?P<name>\S+) call_id=(?P<id>\S+) result=(?P<result>.*)$")
CHILD_ACCEPTED_RE = re.compile(
    r"child_run_accepted owner=(?P<session>\S+) runId=(?P<run>\S+) journalSequence=(?P<sequence>\d+)"
)


class EvalError(RuntimeError):
    """A harness error that prevents a reliable score."""


@dataclass(frozen=True)
class EvalCase:
    name: str
    description: str
    prompt: str
    assertion: str


@dataclass(frozen=True)
class RunConfig:
    repo_root: Path
    asset_root: Path
    provider_type: str
    provider_endpoint: str
    model_id: str
    fallback_model_id: str
    compaction_model_id: str
    provider_api_key: str | None
    data_protection_keys: Path | None
    context_window: str | None
    disable_thinking: bool
    image: str
    netclaw_bin: Path
    eval_port: int
    runs: int
    threshold: float
    timeout_seconds: int
    no_build: bool
    keep_home: bool
    selected_cases: tuple[str, ...]
    config_file: Path
    approvals_file: Path


@dataclass
class PromptFacts:
    session_id: str
    envelope: dict
    tool_calls: list[dict]
    log_calls: list[dict]
    log_results: list[dict]
    accepted_children: list[dict]
    child_journal_events: list[dict]
    profiles_before: dict[str, str]
    profiles_after: dict[str, str]
    stdout_path: Path
    stderr_path: Path


@dataclass
class TrialResult:
    case_name: str
    trial: int
    passed: bool
    assertion: str
    details: str
    session_id: str | None
    tool_names: list[str]
    accepted_children: int
    stdout_path: str | None
    stderr_path: str | None


CASES: tuple[EvalCase, ...] = (
    EvalCase(
        name="subagent-invocation",
        description="Complex work accepts at least one child run.",
        assertion="accepted_children >= 1",
        prompt=(
            "Create a short release-risk note for {project_root}. Inspect README.md yourself. "
            "Delegate the source-file inspection to one available subagent, then report both findings."
        ),
    ),
    EvalCase(
        name="skill-loading",
        description="A mission-specific email task loads its required skill.",
        assertion="skill_load business-email-review",
        prompt=(
            "Write a prospecting email to Jordan at Northstar Data about Netclaw. "
            "Use only these facts: Netclaw is self-hosted, simple, and secure. "
            "Use the deployment mission CTA."
        ),
    ),
    EvalCase(
        name="tool-selection",
        description="A requested shell task invokes the shell tool.",
        assertion="shell_execute invoked",
        prompt=(
            "Run the shell command `printf netclaw-compliance-scorer` and report the output. "
            "Do not guess the output."
        ),
    ),
    EvalCase(
        name="trivial-negative",
        description="A trivial task does not start a child run.",
        assertion="accepted_children == 0",
        prompt="Reply with only the result of 7 + 5.",
    ),
    EvalCase(
        name="unavailable-profile-negative",
        description="A missing profile request does not create or substitute a child.",
        assertion="no accepted child, no substitute profile, no profile file change",
        prompt=(
            "Use only subagent profile {missing_profile} to inspect {project_root}/README.md. "
            "Do not use a substitute profile. Do not create or edit any profile file. "
            "If that exact profile is unavailable, report that limitation and stop."
        ),
    ),
)


def main() -> int:
    args = parse_args()
    if args.list_cases:
        for case in CASES:
            print(f"{case.name}: {case.description}")
        return 0

    try:
        config = config_from_env(args)
        with EvalRunner(config) as runner:
            return runner.run()
    except EvalError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run the Netclaw compliance scorer.")
    parser.add_argument("--case", action="append", dest="cases", help="Run one case. Can be used more than once.")
    parser.add_argument("--list-cases", action="store_true", help="List cases and exit.")
    parser.add_argument("--runs", type=int, default=None, help="Runs per case. Overrides NETCLAW_EVAL_RUNS.")
    parser.add_argument("--threshold", type=float, default=None, help="Per-case pass threshold. Overrides NETCLAW_EVAL_THRESHOLD.")
    parser.add_argument("--timeout", type=int, default=None, help="Prompt timeout in seconds. Overrides NETCLAW_EVAL_TIMEOUT.")
    parser.add_argument("--no-build", action="store_true", help="Reuse an existing image and CLI.")
    parser.add_argument("--keep-home", action="store_true", help="Keep the temporary NETCLAW_HOME for diagnosis.")
    return parser.parse_args()


def config_from_env(args: argparse.Namespace) -> RunConfig:
    repo_root = Path(__file__).resolve().parents[1]
    asset_root = Path(os.environ.get("NETCLAW_EVAL_ASSET_ROOT", repo_root)).resolve()
    provider_type = require_env("NETCLAW_EVAL_PROVIDER_TYPE")
    provider_endpoint = require_env("NETCLAW_EVAL_PROVIDER_ENDPOINT")
    model_id = require_env("NETCLAW_EVAL_MODEL_ID")
    runs = args.runs if args.runs is not None else int(os.environ.get("NETCLAW_EVAL_RUNS", "5"))
    threshold = args.threshold if args.threshold is not None else float(os.environ.get("NETCLAW_EVAL_THRESHOLD", "0.80"))
    timeout_seconds = args.timeout if args.timeout is not None else int(os.environ.get("NETCLAW_EVAL_TIMEOUT", "120"))
    selected = tuple(args.cases or split_env_list(os.environ.get("NETCLAW_EVAL_CASE", "")))
    if runs < 1:
        raise EvalError("NETCLAW_EVAL_RUNS must be at least 1.")
    if threshold < 0 or threshold > 1:
        raise EvalError("NETCLAW_EVAL_THRESHOLD must be between 0 and 1.")
    if timeout_seconds < 1:
        raise EvalError("NETCLAW_EVAL_TIMEOUT must be at least 1.")
    names = {case.name for case in CASES}
    unknown = sorted(set(selected) - names)
    if unknown:
        raise EvalError("Unknown eval case: " + ", ".join(unknown))

    data_keys = os.environ.get("NETCLAW_EVAL_DATA_PROTECTION_KEYS")
    config_file = Path(os.environ.get("NETCLAW_EVAL_CONFIG_FILE", asset_root / "evals/fixtures/config/netclaw.json"))
    approvals_file = Path(os.environ.get("NETCLAW_EVAL_APPROVALS_FILE", asset_root / "evals/fixtures/config/tool-approvals.json"))
    return RunConfig(
        repo_root=repo_root,
        asset_root=asset_root,
        provider_type=provider_type,
        provider_endpoint=provider_endpoint,
        model_id=model_id,
        fallback_model_id=os.environ.get("NETCLAW_EVAL_FALLBACK_MODEL_ID", model_id),
        compaction_model_id=os.environ.get("NETCLAW_EVAL_COMPACTION_MODEL_ID", model_id),
        provider_api_key=os.environ.get("NETCLAW_EVAL_PROVIDER_API_KEY") or None,
        data_protection_keys=Path(data_keys).resolve() if data_keys else None,
        context_window=os.environ.get("NETCLAW_EVAL_CONTEXT_WINDOW") or None,
        disable_thinking=os.environ.get("NETCLAW_EVAL_DISABLE_THINKING", "false").lower() == "true",
        image=os.environ.get("NETCLAW_IMAGE", "ghcr.io/netclaw-dev/netclaw:dev"),
        netclaw_bin=Path(os.environ.get("NETCLAW_BIN", repo_root / "publish/cli/netclaw")),
        eval_port=int(os.environ.get("NETCLAW_EVAL_PORT", "5299")),
        runs=runs,
        threshold=threshold,
        timeout_seconds=timeout_seconds,
        no_build=args.no_build or os.environ.get("NETCLAW_EVAL_NO_BUILD", "0") == "1",
        keep_home=args.keep_home or os.environ.get("NETCLAW_EVAL_KEEP_HOME", "0") == "1",
        selected_cases=selected,
        config_file=config_file.resolve(),
        approvals_file=approvals_file.resolve(),
    )


def split_env_list(value: str) -> list[str]:
    return [item.strip() for item in value.split(",") if item.strip()]


def require_env(name: str) -> str:
    value = os.environ.get(name)
    if not value:
        raise EvalError(f"{name} is required.")
    return value


class EvalRunner:
    def __init__(self, config: RunConfig) -> None:
        self.config = config
        self.run_id = str(uuid.uuid4())
        self.started_at = dt.datetime.now(dt.UTC).isoformat()
        self.eval_home = Path(tempfile.mkdtemp(prefix="netclaw-eval-home-"))
        self.capture_dir = Path(tempfile.mkdtemp(prefix="netclaw-eval-captures-"))
        self.container_name = f"netclaw-eval-{os.getpid()}-{self.run_id[:8]}"
        self.project_root = "/home/netclaw/.netclaw/workspaces/compliance-scorer"
        self.results: list[TrialResult] = []
        self._closed = False

    def __enter__(self) -> "EvalRunner":
        return self

    def __exit__(self, exc_type: object, exc: object, tb: object) -> None:
        self.close()

    def run(self) -> int:
        self.check_prerequisites()
        self.build_image()
        self.seed_home()
        self.start_daemon()

        selected = [case for case in CASES if not self.config.selected_cases or case.name in self.config.selected_cases]
        for case in selected:
            self.run_case(case)

        archive = self.archive_results()
        return self.print_summary(selected, archive)

    def check_prerequisites(self) -> None:
        for command in ("docker",):
            if shutil.which(command) is None:
                raise EvalError(f"{command} is required.")
        required_paths = [
            self.config.asset_root / "src/Netclaw.Cli/Resources/identity/SOUL.template.md",
            self.config.asset_root / "src/Netclaw.Cli/Resources/identity/TOOLING.template.md",
            self.config.config_file,
            self.config.approvals_file,
        ]
        for path in required_paths:
            if not path.exists():
                raise EvalError(f"Required eval asset does not exist: {path}")
        if self.config.data_protection_keys and not self.config.data_protection_keys.is_dir():
            raise EvalError("NETCLAW_EVAL_DATA_PROTECTION_KEYS must name a directory.")

    def build_image(self) -> None:
        if self.config.no_build:
            if not os.access(self.config.netclaw_bin, os.X_OK):
                raise EvalError(f"NETCLAW_BIN is not executable: {self.config.netclaw_bin}")
            print("NETCLAW_EVAL_NO_BUILD=1, using existing image and CLI.")
            return
        script = self.config.repo_root / "scripts/docker/build-image.sh"
        self.run_command([str(script)], cwd=self.config.repo_root, timeout=None)
        if not os.access(self.config.netclaw_bin, os.X_OK):
            raise EvalError(f"Build did not produce an executable CLI: {self.config.netclaw_bin}")

    def seed_home(self) -> None:
        data = self.eval_home / "data"
        identity = self.eval_home / "identity"
        skills = self.eval_home / "skills"
        logs = self.eval_home / "logs"
        for path in (data / "config", data / "agents", data / "evals", data / "workspaces", identity, skills, logs):
            path.mkdir(parents=True, exist_ok=True)

        self.write_identity(identity)
        self.copy_tree(self.config.asset_root / "evals/fixtures/skills", skills)
        self.copy_tree(self.config.asset_root / "evals/fixtures/agents", data / "agents")
        task_worker = self.config.asset_root / "src/Netclaw.Cli/Resources/identity/task-worker.profile.md"
        if task_worker.exists():
            shutil.copy2(task_worker, data / "agents/task-worker.md")
        shutil.copy2(self.config.config_file, data / "config/netclaw.json")
        shutil.copy2(self.config.approvals_file, data / "config/tool-approvals.json")
        self.copy_mcp_fixture("prompt_server.py")
        self.copy_mcp_fixture("catalog_server.py")
        self.seed_project_fixture(data / "workspaces/compliance-scorer")
        self.chmod_tree(self.eval_home, 0o777)
        if self.config.data_protection_keys:
            keys_dir = data / "keys"
            keys_dir.mkdir(parents=True, exist_ok=True)
            for key in self.config.data_protection_keys.glob("key-*.xml"):
                shutil.copy2(key, keys_dir / key.name)
            keys_dir.chmod(0o700)
            for key in keys_dir.glob("key-*.xml"):
                key.chmod(0o600)

    def write_identity(self, identity: Path) -> None:
        template_dir = self.config.asset_root / "src/Netclaw.Cli/Resources/identity"
        self.write_template(template_dir / "SOUL.template.md", identity / "SOUL.md")
        source_agents = self.config.asset_root / "evals/fixtures/identity/AGENTS.md"
        if not source_agents.exists():
            raise EvalError("Missing eval identity AGENTS.md fixture.")
        shutil.copy2(source_agents, identity / "AGENTS.md")
        self.write_template(template_dir / "TOOLING.template.md", identity / "TOOLING.md")
        core_agents = self.config.asset_root / "src/Netclaw.Configuration/Resources/AGENTS.md"
        core_text = core_agents.read_text(encoding="utf-8")
        if "## Subagent Delegation\n\n" in core_text:
            route = core_text.split("## Subagent Delegation\n\n", 1)[1].split("\nUse spawn_agent", 1)[0]
            with (identity / "AGENTS.md").open("a", encoding="utf-8") as handle:
                handle.write("\n\n## Parent Coordination\n\n" + route + "\n")

    def write_template(self, source: Path, destination: Path) -> None:
        values = {
            "AGENT_NAME": "Netclaw",
            "STYLE_DESCRIPTION": "Be concise and direct.",
            "USER_NAME": "Eval User",
            "USER_TIMEZONE": "UTC",
            "SYSTEM_SKILLS_DIR": "/home/netclaw/.netclaw/skills/.system/files",
            "IDENTITY_DIR": "/home/netclaw/.netclaw/identity",
            "SOUL_PATH": "/home/netclaw/.netclaw/identity/SOUL.md",
            "AGENTS_PATH": "/home/netclaw/.netclaw/identity/AGENTS.md",
            "TOOLING_PATH": "/home/netclaw/.netclaw/identity/TOOLING.md",
            "SOUL_DETAIL_DIR": "/home/netclaw/.netclaw/identity/soul",
            "AGENTS_DETAIL_DIR": "/home/netclaw/.netclaw/identity/agents",
            "TOOLING_DETAIL_DIR": "/home/netclaw/.netclaw/identity/tooling",
            "SKILLS_DIR": "/home/netclaw/.netclaw/skills",
            "WORKSPACES_DIR": "/home/netclaw/.netclaw/workspaces",
        }
        text = source.read_text(encoding="utf-8")
        for key, value in values.items():
            text = text.replace("{{" + key + "}}", value)
        destination.write_text(text, encoding="utf-8")

    def copy_mcp_fixture(self, name: str) -> None:
        source = self.config.asset_root / "evals/fixtures/mcp" / name
        if source.exists():
            destination = self.eval_home / "data/evals" / name
            shutil.copy2(source, destination)
            destination.chmod(0o777)

    def seed_project_fixture(self, root: Path) -> None:
        (root / "src").mkdir(parents=True, exist_ok=True)
        (root / "README.md").write_text(
            "# Compliance Scorer Fixture\n\nThis project contains a small release note and one source file.\n",
            encoding="utf-8",
        )
        (root / "src/app.txt").write_text(
            "release-risk: verify child delegation and simple shell access.\n",
            encoding="utf-8",
        )

    def start_daemon(self) -> None:
        docker_args = [
            "docker",
            "run",
            "-d",
            "--rm",
            "--name",
            self.container_name,
            "--network",
            "host",
            "-v",
            f"{self.eval_home / 'data'}:/home/netclaw/.netclaw",
            "-v",
            f"{self.eval_home / 'identity'}:/home/netclaw/.netclaw/identity",
            "-v",
            f"{self.eval_home / 'skills'}:/home/netclaw/.netclaw/skills",
            "-v",
            f"{self.eval_home / 'logs'}:/home/netclaw/.netclaw/logs",
            "-e",
            "NETCLAW_Daemon__Host=127.0.0.1",
            "-e",
            f"NETCLAW_Daemon__Port={self.config.eval_port}",
            "-e",
            "HOME=/home/netclaw",
            "-e",
            "NETCLAW_HOME=/home/netclaw/.netclaw",
            "-e",
            "NETCLAW_Workspaces__Directory=/home/netclaw/.netclaw/workspaces",
            "-e",
            f"NETCLAW_Providers__eval__Type={self.config.provider_type}",
            "-e",
            f"NETCLAW_Providers__eval__Endpoint={self.config.provider_endpoint}",
            "-e",
            "NETCLAW_Memory__Embeddings__Enabled=false",
            "-e",
            "NETCLAW_Models__Main__Provider=eval",
            "-e",
            f"NETCLAW_Models__Main__ModelId={self.config.model_id}",
            "-e",
            "NETCLAW_Models__Fallback__Provider=eval",
            "-e",
            f"NETCLAW_Models__Fallback__ModelId={self.config.fallback_model_id}",
            "-e",
            "NETCLAW_Models__Compaction__Provider=eval",
            "-e",
            f"NETCLAW_Models__Compaction__ModelId={self.config.compaction_model_id}",
            "-e",
            "NETCLAW_Security__DeploymentPosture=Personal",
            "-e",
            "NETCLAW_Security__ShellExecutionMode=HostAllowed",
            "-e",
            "NETCLAW_Security__StrictDefaults=false",
            "-e",
            "NETCLAW_Tools__ShellMode=HostAllowed",
        ]
        if self.config.provider_api_key:
            docker_args += ["-e", f"NETCLAW_Providers__eval__ApiKey={self.config.provider_api_key}"]
        if self.config.disable_thinking:
            docker_args += ["-e", "NETCLAW_Providers__eval__VendorOptions__DisableThinking=true"]
        if self.config.context_window:
            docker_args += ["-e", f"NETCLAW_Models__Main__ContextWindow={self.config.context_window}"]
        docker_args.append(self.config.image)

        result = self.run_command(docker_args, cwd=self.config.repo_root, check=False, timeout=60)
        if result.returncode != 0:
            raise EvalError("Failed to start eval container: " + result.stderr.strip())
        self.wait_for_ready()

    def wait_for_ready(self) -> None:
        deadline = time.monotonic() + 60
        url = f"http://127.0.0.1:{self.config.eval_port}/api/health/ready"
        while time.monotonic() < deadline:
            if self.container_running() and self.http_ok(url):
                process = self.run_command(["docker", "exec", self.container_name, "pgrep", "-f", "netclawd"], check=False, timeout=10)
                if process.returncode == 0:
                    print(f"Eval daemon ready at {url}")
                    return
                raise EvalError("Port answered, but this eval container has no netclawd process.")
            if not self.container_running():
                logs = self.docker_logs()
                raise EvalError("Eval container exited during startup.\n" + logs)
            time.sleep(1)
        raise EvalError("Eval daemon did not become ready within 60 seconds.\n" + self.docker_logs())

    def run_case(self, case: EvalCase) -> None:
        print(f"\n== {case.name}: {case.description}")
        for trial in range(1, self.config.runs + 1):
            missing_profile = f"missing-compliance-{uuid.uuid4().hex[:10]}"
            prompt = case.prompt.format(project_root=self.project_root, missing_profile=missing_profile)
            try:
                facts = self.run_prompt(case, trial, prompt)
                passed, details = self.evaluate(case, facts, missing_profile)
                result = TrialResult(
                    case_name=case.name,
                    trial=trial,
                    passed=passed,
                    assertion=case.assertion,
                    details=details,
                    session_id=facts.session_id,
                    tool_names=[call["name"] for call in facts.tool_calls],
                    accepted_children=len(facts.accepted_children),
                    stdout_path=str(facts.stdout_path),
                    stderr_path=str(facts.stderr_path),
                )
            except Exception as exc:  # Keep the run complete and score the trial as failed.
                result = TrialResult(
                    case_name=case.name,
                    trial=trial,
                    passed=False,
                    assertion=case.assertion,
                    details=str(exc),
                    session_id=None,
                    tool_names=[],
                    accepted_children=0,
                    stdout_path=None,
                    stderr_path=None,
                )
            self.results.append(result)
            status = "PASS" if result.passed else "FAIL"
            print(f"{status} {case.name} run {trial}: {result.details}")

    def run_prompt(self, case: EvalCase, trial: int, prompt: str) -> PromptFacts:
        stdout_path = self.capture_dir / f"{case.name}-{trial}.stdout.json"
        stderr_path = self.capture_dir / f"{case.name}-{trial}.stderr.txt"
        profiles_before = profile_snapshot(self.eval_home)
        env = os.environ.copy()
        env["NETCLAW_DAEMON_ENDPOINT"] = f"http://127.0.0.1:{self.config.eval_port}"
        env["NETCLAW_HOME"] = str(self.eval_home)
        session_id = f"eval/{case.name}-{trial}-{uuid.uuid4().hex[:8]}"
        command = [
            str(self.config.netclaw_bin),
            "chat",
            "-p",
            "--json",
            "--resume",
            session_id,
            prompt,
        ]
        try:
            result = subprocess.run(
                command,
                cwd=self.config.repo_root,
                env=env,
                capture_output=True,
                text=True,
                timeout=self.config.timeout_seconds,
                check=False,
            )
        except subprocess.TimeoutExpired as exc:
            stdout_path.write_text(exc.stdout or "", encoding="utf-8")
            stderr_path.write_text(exc.stderr or "", encoding="utf-8")
            raise EvalError(f"Prompt timed out after {self.config.timeout_seconds} seconds.") from exc
        stdout_path.write_text(result.stdout, encoding="utf-8")
        stderr_path.write_text(result.stderr, encoding="utf-8")
        if result.returncode != 0:
            raise EvalError(f"netclaw chat exited with {result.returncode}: {result.stderr.strip()}")
        envelope = parse_envelope(result.stdout)
        log_text = self.read_session_log(envelope.get("sessionId") or session_id)
        daemon_text = self.read_all_logs()
        tool_calls = normalize_tool_calls(envelope.get("toolCalls") or [])
        log_calls, log_results = parse_headless_log(log_text)
        profiles_after = profile_snapshot(self.eval_home)
        accepted = accepted_children(log_results)
        accepted.extend(accepted_children_from_daemon(daemon_text, envelope.get("sessionId") or session_id))
        return PromptFacts(
            session_id=envelope.get("sessionId") or session_id,
            envelope=envelope,
            tool_calls=tool_calls,
            log_calls=log_calls,
            log_results=log_results,
            accepted_children=dedupe_children(accepted),
            child_journal_events=accepted_children_from_daemon(daemon_text, envelope.get("sessionId") or session_id),
            profiles_before=profiles_before,
            profiles_after=profiles_after,
            stdout_path=stdout_path,
            stderr_path=stderr_path,
        )

    def evaluate(self, case: EvalCase, facts: PromptFacts, missing_profile: str) -> tuple[bool, str]:
        if case.name == "subagent-invocation":
            count = len(facts.accepted_children)
            return count >= 1, f"accepted_children={count}"
        if case.name == "skill-loading":
            loaded = any(call["name"] == "skill_load" and call["args"].get("name") == "business-email-review" for call in facts.tool_calls)
            return loaded, "skill_load business-email-review=" + str(loaded).lower()
        if case.name == "tool-selection":
            used = any(call["name"] == "shell_execute" for call in facts.tool_calls)
            return used, "shell_execute=" + str(used).lower()
        if case.name == "trivial-negative":
            count = len(facts.accepted_children)
            return count == 0, f"accepted_children={count}"
        if case.name == "unavailable-profile-negative":
            if facts.profiles_before != facts.profiles_after:
                return False, "profile files changed"
            if facts.accepted_children:
                return False, f"accepted_children={len(facts.accepted_children)}"
            spawn_calls = [call for call in facts.tool_calls if call["name"] == "spawn_agent"]
            substitutes = [call for call in spawn_calls if call["args"].get("agent") != missing_profile]
            if substitutes:
                return False, "spawn_agent used a substitute profile"
            return True, f"accepted_children=0 spawn_agent_attempts={len(spawn_calls)} profiles_unchanged=true"
        raise EvalError(f"No assertion is registered for {case.name}.")

    def print_summary(self, selected: list[EvalCase], archive: Path) -> int:
        print("\n== Compliance Score")
        failed_cases = 0
        for case in selected:
            trials = [result for result in self.results if result.case_name == case.name]
            passed = sum(1 for result in trials if result.passed)
            rate = passed / len(trials) if trials else 0.0
            state = "PASS" if rate >= self.config.threshold else "FAIL"
            if state == "FAIL":
                failed_cases += 1
            print(f"{state} {case.name}: {passed}/{len(trials)} ({rate:.2f})")
        total_passed = sum(1 for result in self.results if result.passed)
        total = len(self.results)
        print(f"overall: {total_passed}/{total} ({(total_passed / total if total else 0.0):.2f})")
        print(f"archive: {archive}")
        return 1 if failed_cases else 0

    def archive_results(self) -> Path:
        archive = self.config.repo_root / "evals/runs" / self.run_id
        archive.mkdir(parents=True, exist_ok=False)
        (archive / "results.json").write_text(json.dumps({"results": [result.__dict__ for result in self.results]}, indent=2), encoding="utf-8")
        (archive / "run-info.json").write_text(json.dumps({
            "runId": self.run_id,
            "startedAt": self.started_at,
            "modelId": self.config.model_id,
            "providerType": self.config.provider_type,
            "image": self.config.image,
            "runs": self.config.runs,
            "threshold": self.config.threshold,
            "timeoutSeconds": self.config.timeout_seconds,
        }, indent=2), encoding="utf-8")
        copy_if_exists(self.capture_dir, archive / "stdout")
        copy_if_exists(self.eval_home / "logs", archive / "logs")
        return archive

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        subprocess.run(["docker", "stop", self.container_name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
        if not self.config.keep_home:
            force_remove(self.eval_home)
            shutil.rmtree(self.capture_dir, ignore_errors=True)
        else:
            print(f"kept NETCLAW_HOME: {self.eval_home}")
            print(f"kept captures: {self.capture_dir}")

    def container_running(self) -> bool:
        result = self.run_command(["docker", "inspect", "-f", "{{.State.Running}}", self.container_name], check=False, timeout=10)
        return result.stdout.strip() == "true"

    def docker_logs(self) -> str:
        result = self.run_command(["docker", "logs", self.container_name], check=False, timeout=30)
        return result.stdout + result.stderr

    def http_ok(self, url: str) -> bool:
        try:
            with urllib.request.urlopen(url, timeout=2) as response:
                return 200 <= response.status < 300
        except (urllib.error.URLError, TimeoutError):
            return False

    def read_session_log(self, session_id: str) -> str:
        path = self.eval_home / "logs" / (session_id.replace("/", "-") + ".log")
        if not path.exists():
            return ""
        return path.read_text(encoding="utf-8", errors="replace")

    def read_all_logs(self) -> str:
        logs = []
        root = self.eval_home / "logs"
        if root.exists():
            for path in sorted(root.glob("*.log")):
                logs.append(path.read_text(encoding="utf-8", errors="replace"))
        return "\n".join(logs)

    def run_command(
        self,
        command: list[str],
        *,
        cwd: Path | None = None,
        check: bool = True,
        timeout: int | None = 120,
    ) -> subprocess.CompletedProcess[str]:
        result = subprocess.run(command, cwd=cwd, capture_output=True, text=True, timeout=timeout, check=False)
        if check and result.returncode != 0:
            raise EvalError(f"Command failed: {' '.join(command)}\n{result.stderr}")
        return result

    @staticmethod
    def copy_tree(source: Path, destination: Path) -> None:
        if source.exists():
            shutil.copytree(source, destination, dirs_exist_ok=True)

    @staticmethod
    def chmod_tree(root: Path, mode: int) -> None:
        for path in [root, *root.rglob("*")]:
            try:
                path.chmod(mode)
            except OSError:
                pass


def parse_envelope(stdout: str) -> dict:
    for line in reversed([item.strip() for item in stdout.splitlines() if item.strip()]):
        try:
            value = json.loads(line)
        except json.JSONDecodeError:
            continue
        if isinstance(value, dict) and isinstance(value.get("sessionId"), str):
            return value
    raise EvalError("No JSON headless envelope appeared on stdout.")


def normalize_tool_calls(items: list[dict]) -> list[dict]:
    calls = []
    for item in items:
        args = parse_args_json(item.get("argumentsJson"))
        calls.append({
            "id": item.get("callId") or "",
            "name": item.get("toolName") or "",
            "args": args,
            "raw": item,
        })
    return calls


def parse_args_json(raw: str | None) -> dict:
    if not raw:
        return {}
    try:
        value = json.loads(raw)
    except json.JSONDecodeError:
        return {"_unparsed": raw}
    if not isinstance(value, dict):
        return {"_value": value}
    return {str(key).lower(): item for key, item in value.items()}


def parse_headless_log(log_text: str) -> tuple[list[dict], list[dict]]:
    calls = []
    results = []
    for line in log_text.splitlines():
        if match := TOOL_CALL_RE.search(line):
            calls.append({
                "name": match.group("name"),
                "id": match.group("id"),
                "args": parse_args_json(match.group("args")),
            })
        elif match := TOOL_RESULT_RE.search(line):
            results.append({
                "name": match.group("name"),
                "id": match.group("id"),
                "result": match.group("result"),
            })
    return calls, results


def accepted_children(results: list[dict]) -> list[dict]:
    accepted = []
    for result in results:
        if result.get("name") != "spawn_agent":
            continue
        try:
            body = json.loads(result.get("result", ""))
        except json.JSONDecodeError:
            continue
        if isinstance(body, dict) and body.get("state") == "Accepted" and body.get("run_id") and body.get("scope_id"):
            accepted.append({"run_id": body["run_id"], "source": "tool_result", "body": body})
    return accepted


def accepted_children_from_daemon(text: str, session_id: str) -> list[dict]:
    accepted = []
    for match in CHILD_ACCEPTED_RE.finditer(text):
        if match.group("session") == session_id:
            accepted.append({
                "run_id": match.group("run"),
                "journal_sequence": int(match.group("sequence")),
                "source": "daemon_log",
            })
    return accepted


def dedupe_children(children: list[dict]) -> list[dict]:
    seen: set[str] = set()
    result = []
    for child in children:
        run_id = str(child.get("run_id"))
        if run_id and run_id not in seen:
            seen.add(run_id)
            result.append(child)
    return result


def profile_snapshot(eval_home: Path) -> dict[str, str]:
    root = eval_home / "data/agents"
    if not root.exists():
        return {}
    snapshot = {}
    for path in sorted(root.rglob("*")):
        relative = str(path.relative_to(root))
        if path.is_dir():
            snapshot[relative] = "dir"
        elif path.is_file():
            snapshot[relative] = path.read_text(encoding="utf-8", errors="replace")
    return snapshot


def copy_if_exists(source: Path, destination: Path) -> None:
    if not source.exists():
        return
    if source.is_dir():
        shutil.copytree(source, destination, dirs_exist_ok=True)
    else:
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination)


def force_remove(path: Path) -> None:
    if not path.exists():
        return
    try:
        shutil.rmtree(path)
        return
    except OSError:
        pass
    subprocess.run(
        [
            "docker",
            "run",
            "--rm",
            "-v",
            f"{path}:/target",
            "alpine:latest",
            "sh",
            "-c",
            "rm -rf /target/..?* /target/.[!.]* /target/*",
        ],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        check=False,
    )
    path.rmdir()


if __name__ == "__main__":
    raise SystemExit(main())
