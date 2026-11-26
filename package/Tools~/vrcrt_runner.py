#!/usr/bin/env python3
"""VRC Remote Test runner: fixed test procedures on top of vrcrt.sh.

deploy-collect: save the state before a deploy, read Unity's deploy result, wait until every
declared client is ready (and, for worlds that log one, until each client shows this build's
tag), collect evidence per client generation, and write the whole run to run.json. It never
closes or launches a client. Whether a test passed is not decided here.

leave-rejoin (R1b, validated on Windows with Bridge 0.12.0): deploy-collect, then, in the same
continue, close the declared --target-slot with a precondition (Bridge 0.12.0 and later,
slotPreconditions.v1, Relaunch), wait until it has left while the other slots stay as they were
(and, with --remaining-marker, until one of them logs a marker matching that regular expression),
launch it again with a precondition, wait until the new client is back in the same instance, and
collect evidence before the close, after the close and after the rejoin. Nothing is sent again.

manual-steps (R3, docs/plans/r3-contract.md; validated on Windows with two Desktop clients):
deploy-collect, then ask a person the items of a --checklist file one at a time and wait for each
answer (ok, ng or skip, with a memo), given with `answer` here or from the Unity window. Each answer
is recorded with the time, every slot's log position and markers; the slots must stay as they were
ready while it waits. With --leave-rejoin-after ITEM --target-slot N, leave-rejoin's close and
launch run after that item's answer. The answers are recorded as given: the runner judges nothing.

    vrcrt_runner.py prepare --slots 0,1 [--procedure deploy-collect|leave-rejoin] [--target-slot N
                    [--remaining-marker REGEX]] [--expect-clients ...] [--world-build-wait S] [--owner-pid PID] [--run-root DIR]
    vrcrt_runner.py continue --run DIR --claim-token TOKEN
    vrcrt_runner.py run --build headless --project DIR --scene Assets/... --slots 0,1 [--procedure ... --target-slot N
                    [--remaining-marker REGEX]] [--world-build-wait S] [--unity PATH]
    vrcrt_runner.py prepare|run ... --procedure manual-steps --checklist FILE
                    [--leave-rejoin-after ITEM --target-slot N [--remaining-marker REGEX]] [--answer-wait S] [--run-minutes M]
    vrcrt_runner.py waiting --run DIR [--json]
    vrcrt_runner.py answer --run DIR --wait-id ID --item ITEM ok|ng|skip [--memo TEXT] [--by unity|cli]
    vrcrt_runner.py recover --run DIR
    vrcrt_runner.py prune --older-than-days N [--run-root DIR]

prepare prints one JSON line (runId, runDir, worldBuildTag, claimToken). The caller then builds
with RemoteBuildApi in its open Editor, passing runDir and worldBuildTag, and calls continue.
`run` does the three steps itself with a headless Unity, refusing when the project is open.
--world-build-wait (0-600 s, default 120) is how long to wait for the build tag after the clients
are ready; 0 looks once, for worlds that log no tag (their match stays UNKNOWN either way).
--remaining-marker is the caller's own expression; marker lines are only matched against it.

Results are PASS, FAIL, UNKNOWN or CANCELED, also the exit codes 0, 1, 3 and 4 (2: usage). A
missing or stale observation, a full disk or a lost answer is never counted as success.
Python 3.9 or later, standard library only. Log lines and markers are untrusted: they are only
compared with what this run expects, never used to pick a target, a path or a next step.
"""

import argparse
import contextlib
import fcntl
import hashlib
import json
import os
import re
import secrets
import shutil
import signal
import stat
import subprocess
import sys
import tempfile
import time
from datetime import datetime, timedelta, timezone

if sys.version_info < (3, 9):
    sys.stderr.write("vrcrt_runner: Python 3.9 or later is needed (this is %d.%d)\n" % sys.version_info[:2])
    sys.exit(2)

RUNNER_VERSION = "0.14.0"
RUN_SCHEMA = "vrcrt.run"
HERE = os.path.dirname(os.path.realpath(__file__))
FIXTURE_DIR = os.environ.get("VRCRT_FIXTURE_DIR", "")
# Only the script tests replace the CLI, and only together with their fixtures.
CLI = os.environ.get("VRCRT_RUNNER_TEST_CLI") if FIXTURE_DIR and os.environ.get("VRCRT_RUNNER_TEST_CLI") \
    else os.path.join(HERE, "vrcrt.sh")

EXIT_FOR = {"PASS": 0, "FAIL": 1, "UNKNOWN": 3, "CANCELED": 4}
EXIT_USAGE = 2
RUN_ID = re.compile(r"\d{8}T\d{6}Z-[0-9a-f]{8}")
TOKEN = re.compile(r"[0-9a-f]{32}")
TAG = re.compile(r"[A-Za-z0-9_-]{8,64}")
SHA256 = re.compile(r"[0-9a-f]{64}")
BUILD_ID = re.compile(r"\d{8}T\d{9}Z-[0-9a-f]{8}")
INSTANCE_ID = re.compile(r"[0-9a-f]{32}")
EXPECT_BUILD_ID = re.compile(r"[0-9A-Za-z_-]{1,128}")
OPERATION_ID = re.compile(r"[A-Za-z0-9_-]{1,64}")

# Procedures that were checked on real Windows clients, with the runner version that was checked.
# A procedure missing here runs only with --unvalidated (docs/plans/r1-automation.md §5.5).
# leave-rejoin: docs/validation/slot-preconditions.md, 2026-11-26, Bridge 0.12.0+4892f91 (exe SHA-256 a69a379a…bae6e).
# manual-steps: docs/validation/manual-steps.md, 2026-11-26, Bridge 0.12.0+4892f91, two Desktop clients (VR not checked).
VALIDATED_PROCEDURES = {"deploy-collect": "0.11.0", "leave-rejoin": "0.12.0", "manual-steps": "0.13.0"}
PROCEDURES = ("deploy-collect", "leave-rejoin", "manual-steps")


def test_setting(name, default, low, high):
    """A limit only the script tests change (with VRCRT_FIXTURE_DIR set): clamped to [low, high]."""
    if not FIXTURE_DIR:
        return float(default)
    try:
        value = float(os.environ.get(name, default))
    except ValueError:
        return float(default)
    return min(high, max(low, value))


# Limits: all `proposed` in docs/plans/r1-automation.md §4.8 until measured.
READY_SECONDS = test_setting("VRCRT_RUNNER_TEST_READY", "300", 0.5, 300)
READY_SECONDS_FOUR = test_setting("VRCRT_RUNNER_TEST_READY", "420", 0.5, 420)
TAG_SECONDS = test_setting("VRCRT_RUNNER_TEST_TAG", "120", 0.5, 120)  # the default of --world-build-wait
MAX_TAG_WAIT = 600
EXPORT_SECONDS = 120
RUN_SECONDS = test_setting("VRCRT_RUNNER_TEST_RUN", "1800", 1, 1800)
CLI_SECONDS = 60
POLL_SECONDS = test_setting("VRCRT_RUNNER_TEST_POLL", "2", 0.05, 2)
CHECK_SECONDS = 0.5
READ_CHILD_GRACE = 5
WRITE_CHILD_GRACE = 30
STALE_AFTER = timedelta(seconds=15)
MAX_CHILD_OUTPUT = 4 * 1024 * 1024
MAX_PROGRESS_BYTES = 1024 * 1024
MAX_PROGRESS_LINE = 4096
MAX_STEPS = 32
MAX_EVIDENCE_BYTES = 1024 ** 3
MIN_MAC_FREE = int(test_setting("VRCRT_RUNNER_TEST_MAC_FREE", str(2 * 1024 ** 3), 0, 2 ** 62))
MIN_WINDOWS_FREE = 5 * 1024 ** 3
MAX_WINDOWS_EXPORTS = 2 * 1024 ** 3
MAX_JSON_BYTES = 1024 * 1024
# leave-rejoin (R1b), also `proposed`.
LEAVE_SECONDS = test_setting("VRCRT_RUNNER_TEST_LEAVE", "120", 0.5, 120)  # the close seen, the remaining marker
REJOIN_SECONDS = test_setting("VRCRT_RUNNER_TEST_REJOIN", "300", 0.5, 300)  # the new client ready again
CLOSE_CLI_SECONDS = 60  # vrcrt.sh close-client gives the Bridge 45 s
LAUNCH_CLI_SECONDS = 120  # vrcrt.sh launch-client gives the Bridge 90 s
REGEX_SECONDS = 2  # one --remaining-marker match over the new marker lines, in a child that is killed
MAX_REGEX_CHARS = 256
MAX_MARKER_TEXT = 4096
# manual-steps (R3), also `proposed` (docs/plans/r3-contract.md §7).
MAX_ITEMS = 20
MAX_TITLE_CHARS = 120
MAX_ITEM_CHARS = 1000
MAX_CHECKLIST_BYTES = 64 * 1024
MAX_WAITING_BYTES = 16 * 1024
MAX_ANSWER_BYTES = 16 * 1024
MAX_MEMO_CHARS = 2000
MIN_ANSWER_WAIT, DEFAULT_ANSWER_WAIT, MAX_ANSWER_WAIT = 60, 1200, 3600
MIN_RUN_MINUTES, DEFAULT_RUN_MINUTES, MAX_RUN_MINUTES = 30, 90, 120
ANSWER_WAIT_CAP = test_setting("VRCRT_RUNNER_TEST_ANSWER_WAIT", "3600", 0.5, 3600)
MANUAL_RUN_CAP = test_setting("VRCRT_RUNNER_TEST_MANUAL_RUN", "7200", 1, 7200)
MANUAL_RECHECK_SECONDS = test_setting("VRCRT_RUNNER_TEST_MANUAL_RECHECK", "5", 0.05, 5)
OBSERVATION_GRACE_SECONDS = test_setting("VRCRT_RUNNER_TEST_OBSERVATION_GRACE", "15", 0.1, 15)
# deploy-collect --end-marker, also `proposed` (docs/plans/end-marker-contract.md). Checked on
# real clients: docs/validation/end-marker.md, 2026-11-26, Bridge 0.12.0+4892f91, two Desktop clients.
END_MARKER_VALIDATED = True
MIN_END_WAIT, DEFAULT_END_WAIT, MAX_END_WAIT = 60, 900, 1500
END_WAIT_CAP = test_setting("VRCRT_RUNNER_TEST_END_WAIT", "1500", 0.5, 1500)
END_POLL_SECONDS = test_setting("VRCRT_RUNNER_TEST_END_POLL", "5", 0.05, 5)
END_FIELDS = {"pattern", "slots", "waitSeconds", "unvalidated"}


class Stop(Exception):
    """Ends the procedure with a result word and a reason."""

    def __init__(self, result, reason, detail=None):
        super().__init__(reason)
        self.result = result
        self.reason = reason
        self.detail = detail


class Usage(Exception):
    pass


def utc_now():
    return datetime.now(timezone.utc)


def iso(moment=None):
    return (moment or utc_now()).isoformat()


def parse_utc(value):
    if not isinstance(value, str):
        return None
    try:
        moment = datetime.fromisoformat(re.sub(r"(\.\d{6})\d+", r"\1", value.replace("Z", "+00:00")))
    except ValueError:
        return None
    return moment if moment.tzinfo else moment.replace(tzinfo=timezone.utc)


def sha256_of(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


# === Where runs live ===

def default_root():
    home = os.path.expanduser("~")
    if sys.platform == "darwin":
        return os.path.join(home, "Library", "Application Support", "VRCRemoteTest")
    return os.path.join(home, ".local", "state", "vrc-remote-test")


def refused_location(path):
    """Why raw evidence must not go to `path`, or None (Git work trees, iCloud and cloud folders,
    ~/Documents and ~/Desktop: whether iCloud syncs those cannot be known from here)."""
    home = os.path.realpath(os.path.expanduser("~"))
    real = os.path.realpath(path)
    for synced in (os.path.join("Library", "Mobile Documents"), os.path.join("Library", "CloudStorage"),
                   "Documents", "Desktop"):
        folder = os.path.join(home, synced)
        if real == folder or real.startswith(folder + os.sep):
            return "under ~/" + synced
    current = real
    while True:
        if os.path.lexists(os.path.join(current, ".git")):
            return "inside the Git work tree " + current
        parent = os.path.dirname(current)
        if parent == current:
            return None
        current = parent


def private_directory(path):
    """Creates (or checks) a folder only its owner can use; refuses links and others' folders."""
    if os.path.islink(path):
        raise Stop("UNKNOWN", "symbolicLink", "%s is a symbolic link" % path)
    os.makedirs(path, mode=0o700, exist_ok=True)
    info = os.lstat(path)
    if info.st_uid != os.getuid():
        raise Stop("UNKNOWN", "notOwner", "%s belongs to another user" % path)
    os.chmod(path, 0o700)


def checked_root(root):
    root = os.path.abspath(root or default_root())
    where = refused_location(root)
    if where:
        raise Usage("the run root is %s; raw evidence stays outside Git and synced folders" % where)
    private_directory(root)
    for name in ("runs", "locks"):
        private_directory(os.path.join(root, name))
    return root


def run_directory(path):
    """A run folder made by prepare: runs/<runId> under a valid root, not a link, with run.json."""
    path = os.path.abspath(path)
    name = os.path.basename(path)
    parent = os.path.dirname(path)
    if not RUN_ID.fullmatch(name) or os.path.basename(parent) != "runs":
        raise Usage("%s is not a run folder (runs/<runId>)" % path)
    if os.path.islink(path) or os.path.islink(parent) or not os.path.isdir(path):
        raise Usage("%s is not a run folder" % path)
    checked_root(os.path.dirname(parent))
    if os.lstat(path).st_uid != os.getuid():
        raise Usage("%s belongs to another user" % path)
    return path


def write_json(path, value):
    """Unique temporary file in the same folder, flushed, then renamed over `path` (our own file)."""
    directory = os.path.dirname(path)
    descriptor, temporary = tempfile.mkstemp(prefix="." + os.path.basename(path) + ".", suffix=".part", dir=directory)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
            json.dump(value, stream, indent=2, ensure_ascii=True)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(temporary, 0o600)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def write_new(path, data):
    """A new evidence file; never replaces one (O_EXCL, no links followed)."""
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(descriptor, "wb") as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())


def read_json(path, limit=MAX_JSON_BYTES):
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
    with os.fdopen(descriptor, "rb") as stream:
        data = stream.read(limit + 1)
    if len(data) > limit:
        raise ValueError("%s is larger than %d bytes" % (os.path.basename(path), limit))
    return json.loads(data.decode("utf-8"))


# === Processes ===

def process_start(pid):
    """The process's start time, or None when it does not exist: with the PID, which process it is.
    From the kernel (sysctl on macOS, /proc on Linux); ps only when neither answers."""
    if not isinstance(pid, int) or pid <= 0:
        return None
    if sys.platform == "darwin":
        try:
            import ctypes
            import ctypes.util
            import struct
            libc = ctypes.CDLL(ctypes.util.find_library("c"), use_errno=True)
            mib = (ctypes.c_int * 4)(1, 14, 1, pid)  # CTL_KERN, KERN_PROC, KERN_PROC_PID
            buffer = ctypes.create_string_buffer(1024)
            size = ctypes.c_size_t(1024)
            if libc.sysctl(mib, 4, buffer, ctypes.byref(size), None, ctypes.c_size_t(0)) == 0:
                if size.value < 16:
                    return None
                seconds, microseconds = struct.unpack_from("qi", buffer.raw, 0)  # kinfo_proc.kp_proc.p_starttime
                return "%d.%06d" % (seconds, microseconds) if seconds > 0 else None
        except (OSError, AttributeError, ValueError):
            pass
    elif os.path.exists("/proc/self/stat"):
        try:
            with open("/proc/%d/stat" % pid) as stream:
                return stream.read().rsplit(")", 1)[1].split()[19]
        except (OSError, IndexError):
            return None
    try:
        result = subprocess.run(["/bin/ps", "-o", "lstart=", "-p", str(pid)], capture_output=True, text=True,
                                timeout=10, env={"LANG": "C", "LC_ALL": "C", "PATH": "/usr/bin:/bin"})
    except (OSError, subprocess.TimeoutExpired):
        return None
    text = result.stdout.strip()
    return text or None


def same_process(pid, start):
    return start is not None and process_start(pid) == start


def child_environment(extra=None):
    """What children get: never a token in the Unity-to-runner case, only the few names they need."""
    keep = ("PATH", "HOME", "LANG", "LC_ALL", "TMPDIR", "VRCRT_BRIDGE_URL", "VRCRT_CERT_SHA256", "VRCRT_PYTHON")
    environment = {name: os.environ[name] for name in keep if os.environ.get(name)}
    environment.setdefault("PATH", "/usr/bin:/bin:/usr/sbin:/sbin")
    if os.environ.get("VRCRT_TOKEN"):
        environment["VRCRT_TOKEN"] = os.environ["VRCRT_TOKEN"]
    if FIXTURE_DIR:
        for name, value in os.environ.items():
            if name.startswith("VRCRT_FIXTURE") or name.startswith("VRCRT_TEST_"):
                environment[name] = value
    environment.update(extra or {})
    return environment


MAX_CHILD_STDERR = 256 * 1024
MAX_UNITY_LOG = 512 * 1024 * 1024
PREPARE_SECONDS = test_setting("VRCRT_RUNNER_TEST_PREPARE", "60", 0.2, 60)


# === The run and its record ===

class Run:
    def __init__(self, directory):
        self.directory = directory
        self.path = os.path.join(directory, "run.json")
        self.record = read_json(self.path) if os.path.exists(self.path) else None
        self.progress_bytes = 0
        self.evidence_bytes = 0
        # The whole run has one deadline, fixed at prepare: continue uses what is left of it, so
        # it ends when the claim it holds expires.
        end = parse_utc((self.record or {}).get("deadlineUtc"))
        remaining = (end - utc_now()).total_seconds() if end else RUN_SECONDS
        self.deadline = time.monotonic() + max(0.0, remaining)

    def save(self):
        self.record["updatedAtUtc"] = iso()
        write_json(self.path, self.record)

    def progress(self, kind, message, **fields):
        """One JSON line for the window (progress.jsonl), bounded per line and in total."""
        line = json.dumps(dict({"atUtc": iso(), "kind": kind, "message": message}, **fields), ensure_ascii=True)
        line = line[:MAX_PROGRESS_LINE]
        if self.progress_bytes + len(line) + 1 > MAX_PROGRESS_BYTES:
            return
        self.progress_bytes += len(line) + 1
        descriptor = os.open(os.path.join(self.directory, "progress.jsonl"),
                             os.O_WRONLY | os.O_APPEND | os.O_CREAT | os.O_NOFOLLOW, 0o600)
        with os.fdopen(descriptor, "a", encoding="utf-8") as stream:
            stream.write(line + "\n")

    def begin(self, name, **fields):
        """Written before the step starts: a crash leaves it in progress, for recover."""
        operations = self.record.setdefault("operations", [])
        if len(operations) >= MAX_STEPS:
            raise Stop("UNKNOWN", "tooManySteps")
        operation = dict({"name": name, "startedAtUtc": iso(), "result": "UNKNOWN", "inProgress": True}, **fields)
        operations.append(operation)
        self.save()
        self.progress("step", name)
        return operation

    def end(self, operation, result, reason=None, **fields):
        operation.update(fields)
        operation.update({"finishedAtUtc": iso(), "result": result, "inProgress": False})
        if reason:
            operation["reason"] = reason
        self.save()

    def cancelled(self):
        return os.path.exists(os.path.join(self.directory, "cancel.request"))

    def check(self):
        if self.cancelled():
            raise Stop("CANCELED", "canceled")
        if time.monotonic() > self.deadline:
            raise Stop("UNKNOWN", "runDeadline")

    def wait(self, seconds):
        end = time.monotonic() + seconds
        while time.monotonic() < end:
            self.check()
            time.sleep(min(CHECK_SECONDS, max(0.0, end - time.monotonic())))
        self.check()

    def finish(self, result, reason=None, detail=None):
        self.record["state"] = "finished"
        self.record["result"] = result
        self.record["reason"] = reason
        if detail:
            self.record["detail"] = str(detail)[:2000]
        self.record["finishedAtUtc"] = iso()
        for operation in self.record.get("operations", []):
            if operation.get("inProgress"):
                operation.update({"inProgress": False, "finishedAtUtc": iso(), "result": "UNKNOWN",
                                  "reason": operation.get("reason") or reason})
        # A leave-rejoin that ended before its own verdict (not started, stopped, interrupted) is
        # never PASS: CANCELED when the run was, otherwise UNKNOWN.
        procedure = self.record.get("procedure") if isinstance(self.record.get("procedure"), dict) else {}
        results = self.record.get("results")
        if procedure.get("name") == "leave-rejoin" and isinstance(results, dict) and results.get("leaveRejoin") is None:
            results["leaveRejoin"] = "CANCELED" if result == "CANCELED" else "UNKNOWN"
            if not isinstance(self.record.get("leaveRejoin"), dict):
                self.record["leaveRejoin"] = {"state": "notStarted"}
        if procedure.get("name") == "manual-steps":
            finish_manual(self.record, self.directory, result)
        # An end-marker wait that never gave its own word: not reached when it never started,
        # otherwise CANCELED or UNKNOWN, and its record closed with this run's reason (also for
        # recover and the next prepare's takeover, which finish here too).
        if isinstance(results, dict) and "endMarker" in results:
            state = self.record.get("endMarker")
            started = isinstance(state, dict)
            if results["endMarker"] is None:
                results["endMarker"] = ("CANCELED" if result == "CANCELED" else "UNKNOWN") if started else "notReached"
            if started and state.get("endedAtUtc") is None:
                state["endedAtUtc"] = iso()
                state["reason"] = state.get("reason") or reason or result
        self.save()
        self.progress("finished", result, reason=reason)


@contextlib.contextmanager
def step(run, name, **fields):
    """One recorded operation: begun (saved) before its body, ended PASS unless the body ended it
    itself; a Stop ends it with the Stop's word and reason and goes on up."""
    operation = run.begin(name, **fields)
    try:
        yield operation
    except Stop as stop:
        if operation.get("inProgress"):
            run.end(operation, stop.result, stop.reason, detail=stop.detail)
        raise
    if operation.get("inProgress"):
        run.end(operation, "PASS")


# === Children ===

def group_alive(pgid):
    """True while any process of the group exists (or it cannot be told)."""
    try:
        os.killpg(pgid, 0)
    except ProcessLookupError:
        return False
    except (PermissionError, OSError):
        return True
    return True


def stop_group(child, grace):
    """Stops the child and every process left in its group: SIGTERM, then SIGKILL after `grace`
    seconds, and reaps the child. Members that outlive the child (one ignoring SIGTERM) are
    killed too, since the group is ours."""
    pgid = child.pid
    if child.poll() is None or group_alive(pgid):
        try:
            os.killpg(pgid, signal.SIGTERM)
        except ProcessLookupError:
            pass
        end = time.monotonic() + grace
        while time.monotonic() < end and (child.poll() is None or group_alive(pgid)):
            time.sleep(0.05)
        if child.poll() is None or group_alive(pgid):
            try:
                os.killpg(pgid, signal.SIGKILL)
            except ProcessLookupError:
                pass
    try:
        child.wait(grace)
    except subprocess.TimeoutExpired:
        pass
    end = time.monotonic() + 5
    while time.monotonic() < end and group_alive(pgid):
        time.sleep(0.05)
    return not group_alive(pgid) and child.poll() is not None


def size_of(stream_or_path):
    try:
        if isinstance(stream_or_path, str):
            return os.path.getsize(stream_or_path) if os.path.exists(stream_or_path) else 0
        return os.fstat(stream_or_path.fileno()).st_size
    except OSError:
        return 0


def supervise(run, argv, timeout, writes=False, environment=None, watch=None, watch_limit=None):
    """Runs one child with no shell, in its own process group, recorded in run.json first.
    Every 0.5 s: cancel.request, the deadlines and the output sizes. Whatever ends the call (an
    exception or Ctrl-C included), the child and its group are stopped and reaped before it
    returns; a child whose group cannot be confirmed gone stays recorded, so no other run starts
    over it. Returns (exit code, stdout, stderr)."""
    deadline = time.monotonic() + timeout
    if run:
        deadline = min(deadline, run.deadline)
        run.check()  # a cancel or a deadline already passed starts nothing
    out = tempfile.TemporaryFile()
    err = tempfile.TemporaryFile()
    child = None
    stop = None
    gone = True
    try:
        child = subprocess.Popen(argv, stdin=subprocess.DEVNULL, stdout=out, stderr=err, start_new_session=True,
                                 env=environment if environment is not None else child_environment())
        if run:
            run.record.setdefault("children", []).append(
                {"pid": child.pid, "start": process_start(child.pid), "pgid": child.pid, "argv0": os.path.basename(argv[0])})
            run.save()
        while True:
            try:
                child.wait(timeout=CHECK_SECONDS)
            except subprocess.TimeoutExpired:
                pass
            if run and run.cancelled():
                stop = ("CANCELED", "canceled")
            elif time.monotonic() > deadline:
                stop = ("UNKNOWN", "childDeadline")
            elif (size_of(out) > MAX_CHILD_OUTPUT or size_of(err) > MAX_CHILD_STDERR
                  or (watch and size_of(watch) > watch_limit)):
                stop = ("UNKNOWN", "childOutputTooLarge")
            # Checked once more after the child has exited, so a quick child cannot skip them.
            if stop or child.poll() is not None:
                break
    finally:
        if child is not None:
            # Ctrl-C or SIGTERM during the cleanup waits until it is done, so a child is never
            # left behind half-stopped; the signal is delivered right after.
            blocked = signal.pthread_sigmask(signal.SIG_BLOCK, {signal.SIGINT, signal.SIGTERM})
            try:
                gone = stop_group(child, WRITE_CHILD_GRACE if writes else READ_CHILD_GRACE)
                if run and gone:
                    run.record["children"] = [c for c in run.record.get("children", []) if c.get("pid") != child.pid]
                    try:
                        run.save()
                    except OSError:
                        pass
            finally:
                signal.pthread_sigmask(signal.SIG_SETMASK, blocked)
    try:
        if stop:
            raise Stop(stop[0], stop[1], "%s stopped" % os.path.basename(argv[0]))
        if not gone:
            raise Stop("UNKNOWN", "childStillRunning", "%s did not stop" % os.path.basename(argv[0]))
        if size_of(out) > MAX_CHILD_OUTPUT or size_of(err) > MAX_CHILD_STDERR or (watch and size_of(watch) > watch_limit):
            raise Stop("UNKNOWN", "childOutputTooLarge")
        out.seek(0)
        err.seek(0)
        return child.returncode, out.read().decode("utf-8", "replace"), err.read().decode("utf-8", "replace")
    finally:
        out.close()
        err.close()


def cli(run, *args, timeout=CLI_SECONDS):
    return supervise(run, ["/bin/bash", CLI] + list(args), timeout)


def cli_json(run, *args, what, timeout=CLI_SECONDS):
    code, out, err = cli(run, *args, timeout=timeout)
    if code != 0:
        raise Stop("UNKNOWN", what + "Unavailable", err.strip()[-500:])
    try:
        value = json.loads(out)
    except (ValueError, RecursionError):
        raise Stop("UNKNOWN", what + "Invalid")
    if not isinstance(value, dict):
        raise Stop("UNKNOWN", what + "Invalid")
    return value


def children_settled(children):
    """Stops recorded children that are still the same process; True only when none is left.
    A group whose leader is gone but which still has members cannot be tied to us safely, so it
    counts as not settled (nothing is killed on a guess)."""
    settled = True
    for child in children:
        pid, start, pgid = child.get("pid"), child.get("start"), child.get("pgid") or child.get("pid")
        if not isinstance(pid, int) or not isinstance(pgid, int):
            continue
        if same_process(pid, start):
            try:
                os.killpg(pgid, signal.SIGTERM)
            except (ProcessLookupError, PermissionError):
                pass
            end = time.monotonic() + WRITE_CHILD_GRACE
            while time.monotonic() < end and same_process(pid, start):
                time.sleep(CHECK_SECONDS)
            if same_process(pid, start):
                try:
                    os.killpg(pgid, signal.SIGKILL)
                except (ProcessLookupError, PermissionError):
                    pass
                time.sleep(1)
        if same_process(pid, start) or group_alive(pgid):
            settled = False
    return settled


# === Claims: one run per Bridge at a time on this Mac ===

class Claim:
    """locks/<Bridge>.json, changed only under the matching .lock (flock). The owner is the
    caller of prepare (the Unity Editor, or the runner of `run`) until continue takes it over."""

    def __init__(self, root, key=None):
        """`key` is the one saved in a run (recover, continue); otherwise it comes from this
        environment's Bridge URL and certificate pin."""
        if key is None:
            key = hashlib.sha256((os.environ.get("VRCRT_BRIDGE_URL", "") + "|"
                                  + os.environ.get("VRCRT_CERT_SHA256", "").lower()).encode("utf-8")).hexdigest()[:24]
        if not re.fullmatch(r"[0-9a-f]{24}", key):
            raise Usage("the run's claim key is not valid")
        self.key = key
        self.root = root
        self.path = os.path.join(root, "locks", key + ".json")
        self.lock_path = os.path.join(root, "locks", key + ".lock")
        self._descriptor = None

    def __enter__(self):
        self._descriptor = os.open(self.lock_path, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
        fcntl.flock(self._descriptor, fcntl.LOCK_EX)
        return self

    def __exit__(self, *exc):
        os.close(self._descriptor)
        self._descriptor = None

    def read(self):
        try:
            return read_json(self.path, 64 * 1024)
        except FileNotFoundError:
            return None
        except (ValueError, OSError):
            return {"unreadable": True}

    def run_dir(self, current):
        run_id = str((current or {}).get("runId"))
        path = os.path.join(self.root, "runs", run_id)
        return path if RUN_ID.fullmatch(run_id) and os.path.isdir(path) and not os.path.islink(path) else None

    def active(self, current):
        """Why the claim's run must be left alone, or None. Its owner still running, within the
        deadline, holds it; past the deadline a run that is still `running` with its runner alive
        is left alone too (that runner stops itself at its deadline)."""
        if current is None:
            return None
        owner = current.get("owner") or {}
        alive = same_process(owner.get("pid"), owner.get("start"))
        deadline = parse_utc(current.get("deadlineUtc"))
        expired = deadline is None or utc_now() > deadline
        directory = self.run_dir(current)
        state = None
        if directory:
            try:
                state = read_json(os.path.join(directory, "run.json")).get("state")
            except (OSError, ValueError, AttributeError):
                state = None
        if alive and (not expired or state in ("running", "building")):
            return "run %s is still in progress on this Bridge" % current.get("runId")
        return None

    def create(self, run_id, owner_pid, deadline_utc):
        """Under the lock: takes over a claim that is no longer active (finishing its run first),
        then writes a new one. Raises Stop when another run is active."""
        current = self.read()
        if current is not None:
            why = self.active(current) if not current.get("unreadable") else None
            if why:
                raise Stop("UNKNOWN", "otherRunActive", why)
            directory = self.run_dir(current)
            if directory and not finalize_interrupted(directory):
                raise Stop("UNKNOWN", "otherRunChildren", "a process of run %s may still be running" % current.get("runId"))
        token = secrets.token_hex(16)
        write_json(self.path, {"runId": run_id, "token": token, "owner": {"pid": owner_pid, "start": process_start(owner_pid)},
                               "createdAtUtc": iso(), "deadlineUtc": deadline_utc})
        return token

    def start_continue(self, token, run):
        """Under the lock: only the holder of the token, and only once, moves a prepared run to
        running with this process as the owner. Nothing in the run is changed when it refuses."""
        current = self.read()
        if not current or current.get("token") != token or current.get("runId") != run.record.get("runId"):
            raise Stop("UNKNOWN", "claimLost", "this token does not hold the Bridge's claim for this run")
        run.record = read_json(run.path)
        if run.record.get("state") != "prepared":
            raise Stop("UNKNOWN", "notPrepared", "run %s is %s" % (run.record.get("runId"), run.record.get("state")))
        current["owner"] = {"pid": os.getpid(), "start": process_start(os.getpid())}
        write_json(self.path, current)
        run.record["state"] = "running"
        run.save()

    def release(self, token):
        current = self.read()
        if current and current.get("token") == token:
            os.unlink(self.path)


def finalize_interrupted(directory):
    """A run whose runner is gone: its recorded children are stopped when they are still the same
    process, and the run becomes runnerInterrupted UNKNOWN (an expired prepared run too). False,
    with nothing changed, when a child may still be running. Call with the claim lock held."""
    try:
        run = Run(directory)
    except (OSError, ValueError):
        return True
    if not isinstance(run.record, dict) or run.record.get("schema") != RUN_SCHEMA:
        return True
    # Children first, a finished run's too: one that ended with a child it could not stop kept
    # the claim for exactly this check.
    if not children_settled(run.record.get("children") or []):
        return False
    if run.record.get("children"):
        run.record["children"] = []
        run.save()
    if run.record.get("state") != "finished":
        run.finish("UNKNOWN", "runnerInterrupted" if run.record.get("state") in ("running", "building")
                   else "expiredBeforeContinue")
    elif isinstance(run.record.get("manualSteps"), dict) and run.record["manualSteps"].get("waitNotClosed"):
        close_lingering_wait(run.record, directory)
        run.save()
    return True


# === Observations ===

def clients_now(run, timeout=CLI_SECONDS):
    return cli_json(run, "status", "--json", what="clients", timeout=timeout)


def bridge_now(run, timeout=CLI_SECONDS):
    return cli_json(run, "bridge", "--json", what="bridge", timeout=timeout)


def is_count(value, low=0, high=2 ** 63 - 1):
    return type(value) is int and low <= value <= high


def current_batch(info):
    """/bridge's currentBatch as {buildId, launchedSlots}, None when there is no plan, or False when
    it is missing or not that shape."""
    batch = info.get("currentBatch", False)
    if batch is None:
        return None
    if not isinstance(batch, dict):
        return False
    build_id, launched = batch.get("buildId"), batch.get("launchedSlots")
    if not (build_id is None or isinstance(build_id, str)) or not isinstance(launched, list) \
            or not all(is_count(slot, 0, 3) for slot in launched) or len(launched) > 4:
        return False
    return {"buildId": build_id, "launchedSlots": launched}


def slot_preconditions(info):
    """Whether this Bridge takes preconditioned close/launch (slotPreconditions.v1, Bridge 0.12.0).
    vrcrt.sh offers no way to read /capabilities, so the sign is /bridge's fields of the same
    release: bridgeInstanceId, operationRevision and currentBatch, all present and well formed.
    vrcrt.sh close-client/launch-client --expect still read /capabilities themselves and send
    nothing (exit 3) without the feature."""
    instance = info.get("bridgeInstanceId")
    return (isinstance(instance, str) and INSTANCE_ID.fullmatch(instance) is not None
            and is_count(info.get("operationRevision")) and current_batch(info) is not False)


def bridge_identity(info):
    active = info.get("activeConfig") if isinstance(info.get("activeConfig"), dict) else {}
    identity = {name: info.get(name) for name in ("version", "exeSha256", "processStartedAtUtc", "hostGeneration",
                                                  "activeConfigEtag", "savedConfigEtag")}
    identity["mode"] = active.get("VrchatClientMode")
    identity["slotPreconditions"] = slot_preconditions(info)
    if identity["slotPreconditions"]:
        identity.update({"bridgeInstanceId": info["bridgeInstanceId"], "operationRevision": info["operationRevision"],
                         "currentBatch": current_batch(info)})
    return identity


def same_bridge(before, now):
    keys = ("processStartedAtUtc", "hostGeneration", "activeConfigEtag", "savedConfigEtag", "bridgeInstanceId")
    return all(before.get(k) == now.get(k) for k in keys)


def check_guard(info, guard, when):
    """With slotPreconditions.v1 in Relaunch: the same Bridge process, the deploy's launch plan
    still current, and no operation since the one this run last saw (its own close or launch)."""
    if not slot_preconditions(info) or info.get("bridgeInstanceId") != guard["bridgeInstanceId"]:
        raise Stop("UNKNOWN", "bridgeChanged", when)
    # An operation still in the gate (someone else's close, a deploy, a restart) may not have
    # shown in the revision or the clients yet: not a state to pass on.
    if info.get("busy") is not None or info.get("restartPending") is True:
        raise Stop("UNKNOWN", "bridgeBusy", "%s: the Bridge is busy with %s" % (when, info.get("busy") or "a restart"))
    batch = current_batch(info)
    if batch is None or batch.get("buildId") != guard["buildId"]:
        raise Stop("UNKNOWN", "batchChanged", "%s: the Bridge's launch plan is not this deploy's" % when)
    if info.get("operationRevision") != guard["operationRevision"]:
        raise Stop("UNKNOWN", "revisionChanged", "%s: operationRevision %s, this run expected %s"
                   % (when, info.get("operationRevision"), guard["operationRevision"]))


def entry_of(clients, slot):
    for entry in clients.get("clients") or []:
        if isinstance(entry, dict) and entry.get("slot") == slot:
            return entry
    return None


def list_fresh(clients):
    updated = parse_utc(clients.get("updatedAtUtc"))
    return updated is not None and abs(utc_now() - updated) <= STALE_AFTER and clients.get("processListReadable") is True


def slot_state(entry, base, mode):
    """('ready', fixed) or ('wait', reason, observationGap) or ('stop', result, reason) for one slot.

    A slot is ready only from its latest observation: running, its log read through (not catching
    up, an epoch, Logging Full), entered after the deploy (Relaunch: a new generation; Reload: the
    same generation and epoch with a join after the cursor), joined, not in the error world,
    logged in. What cannot be observed is a gap (UNKNOWN at the deadline), never a FAIL."""
    if entry is None:
        return ("wait", "noClient", False)
    if entry.get("running") is not True:
        return ("wait", "notRunning", entry.get("running") is None)
    if entry.get("loginState") == "likelyLoggedOut":
        return ("stop", "UNKNOWN", "likelyLoggedOut")
    if entry.get("loggingLevel") not in (None, "full", "unknown"):
        return ("stop", "UNKNOWN", "loggingNotFull")
    epoch = entry.get("observationEpoch")
    if (entry.get("catchingUp") or entry.get("logStatus") != "shown" or epoch is None
            or entry.get("state") in (None, "unknown")):
        return ("wait", "observationIncomplete", True)
    generation, joined = entry.get("clientGeneration"), entry.get("joinedSeq")
    # The Joining line of this join. A world's load-time markers (Udon Start) come between it and
    # Finished entering world (joinedSeq), so the build tag is looked for from here. None from a
    # Bridge that does not report it: then only lines after joinedSeq count.
    attempt = entry.get("joinAttemptSeq")
    if not (isinstance(attempt, int) and isinstance(joined, int) and attempt <= joined):
        attempt = None
    new_client = base is None or base.get("clientGeneration") != generation
    if new_client:
        if not isinstance(joined, int):
            return ("wait", "notJoinedSinceDeploy", False)
    elif mode == "Relaunch":
        return ("wait", "notRelaunched", False)
    else:
        if base.get("observationEpoch") is None:
            return ("stop", "UNKNOWN", "baselineUnknown")
        if base.get("observationEpoch") != epoch:
            return ("stop", "UNKNOWN", "epochChangedSinceDeploy")
        # The whole join after the cursor: a join that began before the deploy may have loaded the old world.
        if not isinstance(joined, int) or (attempt if attempt is not None else joined) <= (base.get("seq") or 0):
            return ("wait", "notJoinedSinceDeploy", False)
    if entry.get("state") != "joined" or not entry.get("instance"):
        return ("wait", "notJoined", False)
    if entry.get("inErrorWorld") is True:
        return ("wait", "inErrorWorld", False)
    if entry.get("inErrorWorld") is not False:
        return ("wait", "errorWorldUnknown", True)
    if entry.get("loginState") != "authenticated":
        return ("wait", "loginNotConfirmed", True)
    return ("ready", {"clientGeneration": generation, "observationEpoch": epoch, "joinedSeq": joined,
                      "joinAttemptSeq": attempt, "instance": entry.get("instance"), "profile": entry.get("profile"), "processId": entry.get("processId")})


def wait_ready(run, slots, cursor, mode):
    """Every declared slot ready in one instance, from the latest observation; FAIL when the
    observation was complete but a slot never got there, UNKNOWN when it was not."""
    limit = READY_SECONDS_FOUR if len(slots) >= 4 else READY_SECONDS
    end = time.monotonic() + limit
    last = {}
    while True:
        run.check()
        clients = clients_now(run)
        gaps = not list_fresh(clients)
        fixed = {}
        for slot in slots:
            state = slot_state(entry_of(clients, slot), cursor.get(str(slot)), mode)
            if state[0] == "stop":
                entry = entry_of(clients, slot) or {}
                hint = ""
                if state[2] == "likelyLoggedOut":
                    hint = ": log in to VRChat profile %s on Windows once, then run again" % entry.get("profile")
                raise Stop(state[1], state[2], "slot %d%s" % (slot, hint))
            if state[0] == "wait":
                last[slot] = state[1]
                gaps = gaps or state[2]
            else:
                last.pop(slot, None)
                fixed[slot] = state[1]
        if gaps:
            last["observation"] = "incomplete"
        else:
            last.pop("observation", None)
        if len(fixed) == len(slots) and not gaps:
            if len({f["instance"] for f in fixed.values()}) == 1:
                return fixed
            last["instance"] = "differ"
        if time.monotonic() >= end:
            reasons = ", ".join("%s=%s" % (k, v) for k, v in sorted(last.items(), key=str))
            raise Stop("UNKNOWN" if gaps else "FAIL", "notReady", reasons)
        run.progress("wait", "waiting for the clients", reasons={str(k): v for k, v in last.items()})
        run.wait(POLL_SECONDS)


def check_unchanged(entry, want, slot, when):
    """One slot as it was when it became ready (or rejoined), its log read through."""
    if entry is None or entry.get("running") is not True:
        raise Stop("UNKNOWN", "unexpectedLeave", "slot %d %s" % (slot, when))
    if entry.get("catchingUp"):
        raise Stop("UNKNOWN", "observationIncomplete", "slot %d %s" % (slot, when))
    for name in ("clientGeneration", "observationEpoch", "joinedSeq", "instance"):
        if entry.get(name) != want[name]:
            raise Stop("UNKNOWN", "unexpectedChange", "slot %d %s changed %s" % (slot, name, when))
    if want["joinAttemptSeq"] is not None and entry.get("joinAttemptSeq") != want["joinAttemptSeq"]:
        raise Stop("UNKNOWN", "unexpectedChange", "slot %d joinAttemptSeq changed %s" % (slot, when))
    if entry.get("inErrorWorld") is not False or entry.get("loginState") != "authenticated":
        raise Stop("UNKNOWN", "unexpectedChange", "slot %d %s" % (slot, when))


def recheck(run, slots, fixed, bridge_before, when, guard=None, timeout=CLI_SECONDS):
    """All declared slots as they were when they became ready, on the same Bridge host, with
    their logs read through; with a guard (slotPreconditions.v1 in Relaunch) also the same launch
    plan and operation revision. Returns the /bridge and /clients it read."""
    run.check()
    info = bridge_now(run, timeout=timeout)
    if not same_bridge(bridge_before, bridge_identity(info)):
        raise Stop("UNKNOWN", "bridgeChanged", when)
    if guard:
        check_guard(info, guard, when)
    clients = clients_now(run, timeout=timeout)
    if not list_fresh(clients):
        raise Stop("UNKNOWN", "observationStale", when)
    for slot in slots:
        check_unchanged(entry_of(clients, slot), fixed[slot], slot, when)
    return info, clients


def build_tags(text):
    """The vrcrt.build values on one marker line: exact whitespace-separated fields only."""
    return [field[len("vrcrt.build="):] for field in text.split() if field.startswith("vrcrt.build=")]


def wait_world_build(run, slots, fixed, tag, seconds):
    """Each slot shows this build's tag in its current join (fixed generation and epoch, after
    the join's Joining line, or after joinedSeq when the Bridge does not report that line), read
    through. PASS only when every tag seen there is this one; any other tag is FAIL; nothing, two
    tags on a line, or only cut or withheld lines is UNKNOWN. Every slot is read at least once,
    then again until `seconds` have passed."""
    if not tag:
        return "UNKNOWN", "noTagForTheseBytes"
    end = time.monotonic() + seconds
    while True:
        run.check()
        matched, ambiguous = set(), False
        for slot in slots:
            want = fixed[slot]
            markers = cli_json(run, "log", str(slot), "--markers", "--json", what="markers")
            header = markers.get("header") or {}
            if (header.get("clientGeneration") != want["clientGeneration"]
                    or header.get("observationEpoch") != want["observationEpoch"] or header.get("catchingUp")):
                continue
            after = want["joinAttemptSeq"] if want["joinAttemptSeq"] is not None else want["joinedSeq"]
            if (header.get("droppedThroughSeq") or 0) > after:
                return "UNKNOWN", "markersDropped"
            for record in markers.get("records") or []:
                if not isinstance(record, dict) or not isinstance(record.get("seq"), int) or record["seq"] <= after:
                    continue
                if record.get("truncated") or record.get("withheld"):
                    continue
                tags = build_tags(str(record.get("text", "")))
                if any(value != tag for value in tags):
                    return "FAIL", "otherBuildTag"
                if len(tags) > 1:
                    ambiguous = True
                elif tags:
                    matched.add(slot)
        if len(matched) == len(slots) and not ambiguous:
            return "PASS", None
        if time.monotonic() >= end:
            return "UNKNOWN", "ambiguousTag" if ambiguous else "noTagMarker"
        run.wait(POLL_SECONDS)


# === Deploy result ===

DEPLOY_WORDS = ("PASS", "FAIL", "UNKNOWN", "CANCELED")
LAUNCH_WORDS = DEPLOY_WORDS + ("notApplicable", "notReached")


def read_deploy_result(run):
    """Unity's result, checked for the shape this runner uses before anything reads it."""
    path = os.path.join(run.directory, "deploy-result.json")
    try:
        result = read_json(path)
    except FileNotFoundError:
        raise Stop("UNKNOWN", "noDeployResult")
    except (OSError, ValueError):
        raise Stop("UNKNOWN", "deployResultInvalid")
    if not isinstance(result, dict) or result.get("schema") != "vrcrt.deploy-result" or result.get("schemaVersion") != 1:
        raise Stop("UNKNOWN", "deployResultInvalid")
    c, artifact = result.get("classification"), result.get("artifact")
    build_id = result.get("buildId")
    if (not isinstance(c, dict) or c.get("deploy") not in DEPLOY_WORDS or c.get("launch") not in LAUNCH_WORDS
            or not (c.get("stopReason") is None or isinstance(c.get("stopReason"), str))
            or not (artifact is None or isinstance(artifact, dict))
            or not (build_id is None or (isinstance(build_id, str) and BUILD_ID.fullmatch(build_id)))):
        raise Stop("UNKNOWN", "deployResultInvalid")
    tag = (artifact or {}).get("worldBuildTag")
    if not (tag is None or isinstance(tag, str)):
        raise Stop("UNKNOWN", "deployResultInvalid")
    return result


def stop_for_deploy(result):
    """The deploy result's own verdicts decide whether the procedure goes on."""
    c = result["classification"]
    deploy, launch, reason = c["deploy"], c["launch"], c.get("stopReason")
    if deploy != "PASS":
        raise Stop(deploy, "deploy:" + (reason or deploy)[:60])
    if launch == "FAIL":
        raise Stop("FAIL", "launch:" + (reason or "failed")[:60])
    if launch not in ("PASS", "notApplicable") or reason:
        raise Stop("UNKNOWN", "launch:" + (reason or launch)[:60])


# === Commands ===

def parse_slots(text):
    slots = []
    for part in text.split(","):
        part = part.strip()
        if not re.fullmatch(r"[0-3]", part) or int(part) in slots:
            raise Usage("--slots lists 1 to 4 different slots from 0 to 3, e.g. 0,1")
        slots.append(int(part))
    return slots


def parse_expect(items):
    expect = {}
    for item in items or []:
        name, _, value = item.partition("=")
        if name == "count" and re.fullmatch(r"[1-4]", value):
            expect["count"] = int(value)
        elif name == "mode" and value in ("Relaunch", "Reload"):
            expect["mode"] = value
        elif name == "profiles" and re.fullmatch(r"\d{1,2}(,\d{1,2}){0,3}", value):
            expect["profiles"] = [int(v) for v in value.split(",")]
        elif name == "vr" and value in ("Desktop", "VR"):
            expect["vr"] = value
        else:
            raise Usage("--expect-clients takes count=N mode=Relaunch|Reload profiles=0,1 vr=Desktop|VR, not %r" % item)
    return expect


def declaration_problems(expect, info):
    """Like client_plan.py: compare, never change the Bridge's settings."""
    active = info.get("activeConfig") if isinstance(info.get("activeConfig"), dict) else {}
    problems = []
    if "mode" in expect and active.get("VrchatClientMode") != expect["mode"]:
        problems.append("mode is %s" % active.get("VrchatClientMode"))
    if "profiles" in expect and active.get("VrchatProfiles") != expect["profiles"]:
        problems.append("profiles are %s" % active.get("VrchatProfiles"))
    if "count" in expect:
        count = len(active.get("VrchatProfiles") or []) if active.get("VrchatClientMode") == "Relaunch" else active.get("VrchatClientCount")
        if count != expect["count"]:
            problems.append("count is %s" % count)
    if "vr" in expect and active.get("VrchatMode") != expect["vr"]:
        problems.append("VrchatMode is %s" % active.get("VrchatMode"))
    return problems


def valid_regex(text):
    """--remaining-marker: the caller's own expression, bounded, and one Python compiles."""
    if not isinstance(text, str) or not 1 <= len(text) <= MAX_REGEX_CHARS or "\x00" in text:
        return False
    try:
        re.compile(text)
    except (re.error, RecursionError, OverflowError, ValueError):
        return False
    return True


def procedure_parameters(args, slots, expect, wait, checklist=None):
    """The procedure's name and parameters from the command line (all recorded and hashed).
    deploy-collect keeps exactly R1a's parameters, leave-rejoin R1b's. `checklist` is
    manual-steps' (bytes, title, items), already checked."""
    name = getattr(args, "procedure", None) or "deploy-collect"
    target = getattr(args, "target_slot", None)
    marker = getattr(args, "remaining_marker", None)
    after = getattr(args, "leave_rejoin_after", None)
    manual_only = {"--checklist": getattr(args, "checklist", None), "--answer-wait": getattr(args, "answer_wait", None),
                   "--run-minutes": getattr(args, "run_minutes", None), "--leave-rejoin-after": after}
    parameters = {"slots": slots, "expectClients": expect, "worldBuildWaitSeconds": wait}
    end_flags = {"--end-marker": getattr(args, "end_marker", None), "--end-slots": getattr(args, "end_slots", None),
                 "--end-wait": getattr(args, "end_wait", None)}
    if name != "deploy-collect":
        given = [flag for flag, value in end_flags.items() if value is not None]
        if given:
            raise Usage("%s belong to --procedure deploy-collect" % ", ".join(given))
    if name != "manual-steps":
        given = [flag for flag, value in manual_only.items() if value is not None]
        if given:
            raise Usage("%s belong to --procedure manual-steps" % ", ".join(given))
    if name == "deploy-collect":
        if target is not None or marker is not None:
            raise Usage("--target-slot and --remaining-marker belong to --procedure leave-rejoin")
        end = end_marker_parameters(args, slots)
        if end is not None:
            # Only with --end-marker: without it the parameters (and the hash) stay R1a's.
            parameters["endMarker"] = end
        return name, parameters
    if name not in VALIDATED_PROCEDURES and not getattr(args, "unvalidated", False):
        raise Usage("%s has not been validated on real clients yet; add --unvalidated to run it anyway" % name)
    if name == "manual-steps":
        return name, manual_parameters(args, slots, parameters, checklist, target, marker, after)
    if target is None or target not in slots:
        raise Usage("leave-rejoin needs --target-slot N, one of the --slots")
    if len(slots) < 2:
        raise Usage("leave-rejoin needs at least one slot besides --target-slot that stays")
    if marker is not None and not valid_regex(marker):
        raise Usage("--remaining-marker must be a regular expression of 1 to %d characters" % MAX_REGEX_CHARS)
    parameters.update({"targetSlot": target, "remainingMarker": marker,
                       "unvalidated": name not in VALIDATED_PROCEDURES,
                       "leaveWaitSeconds": LEAVE_SECONDS, "rejoinWaitSeconds": REJOIN_SECONDS})
    return name, parameters


def manual_parameters(args, slots, parameters, checklist, target, marker, after):
    if checklist is None:
        raise Usage("manual-steps needs --checklist FILE")
    data, _, items = checklist
    answer = DEFAULT_ANSWER_WAIT if args.answer_wait is None else args.answer_wait
    if not MIN_ANSWER_WAIT <= answer <= MAX_ANSWER_WAIT:
        raise Usage("--answer-wait takes %d to %d seconds" % (MIN_ANSWER_WAIT, MAX_ANSWER_WAIT))
    minutes = DEFAULT_RUN_MINUTES if args.run_minutes is None else args.run_minutes
    if not MIN_RUN_MINUTES <= minutes <= MAX_RUN_MINUTES:
        raise Usage("--run-minutes takes %d to %d" % (MIN_RUN_MINUTES, MAX_RUN_MINUTES))
    parameters.update({"checklistSha256": hashlib.sha256(data).hexdigest(), "itemCount": len(items),
                       "answerWaitSeconds": answer, "runMinutes": minutes, "leaveRejoinAfter": after,
                       "targetSlot": target, "remainingMarker": marker,
                       "unvalidated": "manual-steps" not in VALIDATED_PROCEDURES})
    if after is None:
        if target is not None or marker is not None:
            raise Usage("--target-slot and --remaining-marker go with --leave-rejoin-after")
        return parameters
    if after not in [item["id"] for item in items]:
        raise Usage("--leave-rejoin-after must be the id of one of the checklist's items")
    if target is None or target not in slots:
        raise Usage("--leave-rejoin-after needs --target-slot N, one of the --slots")
    if len(slots) < 2:
        raise Usage("--leave-rejoin-after needs at least one slot besides --target-slot that stays")
    if marker is not None and not valid_regex(marker):
        raise Usage("--remaining-marker must be a regular expression of 1 to %d characters" % MAX_REGEX_CHARS)
    parameters.update({"leaveWaitSeconds": LEAVE_SECONDS, "rejoinWaitSeconds": REJOIN_SECONDS})
    return parameters


def manual_procedure_ok(parameters, slots):
    if not isinstance(parameters, dict) or not isinstance(slots, list) or not slots:
        return False
    if not (("manual-steps" in VALIDATED_PROCEDURES or parameters.get("unvalidated") is True)
            and isinstance(parameters.get("checklistSha256"), str) and SHA256.fullmatch(parameters["checklistSha256"])
            and is_count(parameters.get("itemCount"), 1, MAX_ITEMS)
            and is_count(parameters.get("answerWaitSeconds"), MIN_ANSWER_WAIT, MAX_ANSWER_WAIT)
            and is_count(parameters.get("runMinutes"), MIN_RUN_MINUTES, MAX_RUN_MINUTES)):
        return False
    after, target, marker = parameters.get("leaveRejoinAfter"), parameters.get("targetSlot"), parameters.get("remainingMarker")
    if after is None:
        return target is None and marker is None
    return (isinstance(after, str) and ITEM_ID.fullmatch(after) is not None and len(slots) >= 2
            and is_count(target, 0, 3) and target in slots and (marker is None or valid_regex(marker)))


def procedure_of(record):
    """The procedure run.json names, checked again before continue acts on it."""
    procedure = record.get("procedure") if isinstance(record.get("procedure"), dict) else {}
    name, parameters = procedure.get("name"), procedure.get("parameters")
    # The parameters must be the ones prepare hashed: a valid but changed value (another
    # pattern, a shorter wait, a removed endMarker) is refused as well as a malformed one.
    version = procedure.get("version")
    if not isinstance(version, str) or procedure.get("sha256") != procedure_hash(name, parameters, version):
        raise Stop("UNKNOWN", "procedureInvalid", "run.json's procedure is not the one prepare recorded")
    if name == "deploy-collect" and isinstance(parameters, dict) and (
            "endMarker" not in parameters or end_marker_ok(parameters["endMarker"], record.get("slots"))):
        return name, parameters
    if isinstance(parameters, dict) and "endMarker" in parameters:
        raise Stop("UNKNOWN", "procedureInvalid", "run.json names no procedure this runner can run")
    if name == "manual-steps" and manual_procedure_ok(parameters, record.get("slots")):
        return name, parameters
    slots = record.get("slots")
    if name == "leave-rejoin" and isinstance(parameters, dict) and isinstance(slots, list) and len(slots) >= 2:
        target, marker = parameters.get("targetSlot"), parameters.get("remainingMarker")
        if (is_count(target, 0, 3) and target in slots
                and (name in VALIDATED_PROCEDURES or parameters.get("unvalidated") is True)
                and (marker is None or valid_regex(marker))):
            return name, parameters
    raise Stop("UNKNOWN", "procedureInvalid", "run.json names no procedure this runner can run")


def procedure_seconds(parameters, name, default):
    value = parameters.get(name)
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not 0 < value <= default:
        return default
    return value


def procedure_hash(name, parameters, version=RUNNER_VERSION):
    canonical = json.dumps({"procedure": name, "version": version, "parameters": parameters}, sort_keys=True)
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def tool_versions():
    python = os.path.realpath(sys.executable)
    versions = {"runnerVersion": RUNNER_VERSION, "python": {"path": python, "version": "%d.%d.%d" % sys.version_info[:3]}}
    for name, path in (("runner", os.path.realpath(__file__)), ("vrcrt", CLI), ("pythonExecutable", python)):
        try:
            versions[name + "Sha256"] = sha256_of(path)
        except OSError:
            versions[name + "Sha256"] = None
    return versions


def check_mac_disk(run):
    if shutil.disk_usage(run.directory).free < MIN_MAC_FREE:
        raise Stop("UNKNOWN", "macDiskLow")


def prepare(args, owner_pid):
    root = checked_root(args.run_root)
    slots = parse_slots(args.slots)
    expect = parse_expect(args.expect_clients)
    wait = TAG_SECONDS if args.world_build_wait is None else args.world_build_wait
    if not 0 <= wait <= MAX_TAG_WAIT:
        raise Usage("--world-build-wait takes 0 to %d seconds" % MAX_TAG_WAIT)
    checklist = None
    if getattr(args, "procedure", None) == "manual-steps" and getattr(args, "checklist", None) is not None:
        checklist = load_checklist(args.checklist)
    name, parameters = procedure_parameters(args, slots, expect, wait, checklist)
    created = utc_now()
    run_id = created.strftime("%Y%m%dT%H%M%SZ") + "-" + secrets.token_hex(4)
    # One deadline for the whole run, fixed here: the claim, this Run (a headless build uses it)
    # and continue all go by it.
    seconds = min(parameters["runMinutes"] * 60, MANUAL_RUN_CAP) if name == "manual-steps" else RUN_SECONDS
    deadline_utc = iso(created + timedelta(seconds=seconds))
    directory = os.path.join(root, "runs", run_id)
    os.mkdir(directory, 0o700)
    run = Run(directory)
    run.deadline = time.monotonic() + seconds
    results = {"deploy": None, "launch": None, "clientReadiness": None, "worldBuildMatch": None,
               "evidence": None, "testVerdict": "notJudged"}
    notes = ["PASS means prepared and collected; the test itself is not judged by the runner.",
             "A world build match from markers assumes the world does not fake them."]
    procedure = {"name": name, "version": RUNNER_VERSION, "parameters": parameters,
                 "sha256": procedure_hash(name, parameters)}
    if name == "deploy-collect" and "endMarker" in parameters:
        results["endMarker"] = None
        procedure["endMarkerValidated"] = END_MARKER_VALIDATED
        notes.append("endMarker PASS means each named slot logged a marker line matching --end-marker after it "
                     "joined; the world wrote that line, and the runner does not judge the test.")
    if name == "leave-rejoin":
        results["leaveRejoin"] = None
        procedure["validated"] = name in VALIDATED_PROCEDURES
        notes.append("leave-rejoin PASS means the declared slot left and came back as declared; "
                     "who owns what in the world is not judged by the runner.")
    manual = None
    if name == "manual-steps":
        results["manualSteps"] = None
        if parameters["leaveRejoinAfter"] is not None:
            results["leaveRejoin"] = None
        procedure["validated"] = name in VALIDATED_PROCEDURES
        notes.append("manualSteps is the human answers as given (ok, ng, skip); the runner does not judge the test. "
                     "A human OK never turns a tool result into PASS.")
        _, title, items = checklist
        manual = {"title": title, "checklistSha256": parameters["checklistSha256"],
                  "leaveRejoinAfter": parameters["leaveRejoinAfter"], "result": None, "leaveRejoin": None,
                  "items": [{"index": index, "id": item["id"], "state": "notAsked"} for index, item in enumerate(items, 1)]}
    run.record = {"schema": RUN_SCHEMA, "schemaVersion": 1, "runId": run_id, "createdAtUtc": iso(created),
                  "deadlineUtc": deadline_utc, "state": "preparing",
                  "procedure": procedure,
                  "tools": tool_versions(), "slots": slots, "worldBuildTag": run_id, "operations": [], "evidence": [],
                  "children": [],
                  "results": results,
                  "notes": notes}
    if manual is not None:
        run.record["manualSteps"] = manual
        write_new(os.path.join(directory, "checklist.json"), checklist[0])
    run.save()
    claim = Claim(root)
    token = None
    try:
        with claim:
            token = claim.create(run_id, owner_pid, deadline_utc)
        run.record["claimKey"] = claim.key
        run.save()
        prepare_end = time.monotonic() + PREPARE_SECONDS

        def remaining():
            left = prepare_end - time.monotonic()
            if left <= 0:
                raise Stop("UNKNOWN", "prepareDeadline")
            return left

        operation = run.begin("prepare")
        check_mac_disk(run)
        info = bridge_now(run, timeout=remaining())
        run.record["bridgeBefore"] = bridge_identity(info)
        problems = declaration_problems(expect, info)
        if problems:
            raise Stop("UNKNOWN", "declarationMismatch", "; ".join(problems))
        if name == "leave-rejoin" or (name == "manual-steps" and parameters["leaveRejoinAfter"] is not None):
            # Refused before the deploy: a Bridge without the feature never gets a preconditioned request.
            if not run.record["bridgeBefore"]["slotPreconditions"]:
                raise Stop("UNKNOWN", "slotPreconditionsUnsupported",
                           "the Bridge does not take preconditioned close/launch (0.12.0 and later); nothing was sent")
            if run.record["bridgeBefore"]["mode"] != "Relaunch":
                raise Stop("UNKNOWN", "notRelaunch", "the leave and rejoin needs VrchatClientMode Relaunch")
        clients = clients_now(run, timeout=remaining())
        if not list_fresh(clients):
            raise Stop("UNKNOWN", "observationStale", "before the deploy")
        code, out, err = cli(run, "cursor", timeout=remaining())
        if code != 0:
            raise Stop("UNKNOWN", "cursorUnavailable", err.strip()[-500:])
        try:
            cursor = json.loads(out)
        except (ValueError, RecursionError):
            cursor = None
        if not isinstance(cursor, dict) or not isinstance(cursor.get("slots"), dict):
            raise Stop("UNKNOWN", "cursorInvalid")
        write_new(os.path.join(directory, "before.cursor.json"), out.encode("utf-8"))
        write_new(os.path.join(directory, "clients-before.json"), json.dumps(clients, indent=2).encode("utf-8"))
        run.record["cursor"] = cursor["slots"]
        run.end(operation, "PASS")
        run.record["state"] = "prepared"
        run.save()
    except BaseException as error:
        stop = error if isinstance(error, Stop) else Stop(
            "CANCELED" if isinstance(error, KeyboardInterrupt) else "UNKNOWN",
            "interrupted" if isinstance(error, KeyboardInterrupt) else "prepareFailed", error)
        run.finish(stop.result, stop.reason, stop.detail)
        if token and not run.record.get("children"):
            with claim:
                claim.release(token)
        if isinstance(error, (Stop, KeyboardInterrupt)) or isinstance(error, (OSError, ValueError)):
            raise stop
        raise
    return run, token


def continue_run(directory, token):
    """Takes the claim from the caller of prepare (refusing without changing the run when the
    token does not hold it, or the run is not prepared), then runs the procedure to its end."""
    run = Run(directory)
    record = run.record
    if not isinstance(record, dict) or record.get("schema") != RUN_SCHEMA or record.get("runId") != os.path.basename(directory):
        raise Usage("%s has no run.json of this runner" % directory)
    if not isinstance(record.get("claimKey"), str):
        raise Usage("run %s has no claim key; it was not prepared by this runner" % record.get("runId"))
    claim = Claim(os.path.dirname(os.path.dirname(directory)), record["claimKey"])
    with claim:
        claim.start_continue(token, run)
    try:
        name, _ = procedure_of(run.record)
        result, fixed, guard = deploy_collect(run)
        if name == "leave-rejoin" and result == "PASS":
            result = leave_rejoin(run, fixed, guard)
        elif name == "manual-steps" and result == "PASS":
            result = manual_steps(run, fixed, guard)
        run.finish(result, run.record.get("reason") if result != "PASS" else None)
        return result
    except Stop as stop:
        run.finish(stop.result, stop.reason, stop.detail)
        return stop.result
    except KeyboardInterrupt:
        run.finish("CANCELED", "interrupted")
        return "CANCELED"
    except (OSError, ValueError) as error:
        run.finish("UNKNOWN", "runnerError", error)
        return "UNKNOWN"
    finally:
        # A child that could not be confirmed stopped keeps the claim: the next prepare finds
        # this runner gone and refuses until the child is gone too.
        if not run.record.get("children"):
            with claim:
                claim.release(token)


def deploy_collect(run):
    run.check()  # a cancel written before continue started (the window's Cancel during prepare) ends here
    record = run.record
    slots = record["slots"]
    cursor = record["cursor"]
    results = record["results"]
    bridge_before = record["bridgeBefore"]

    operation = run.begin("readDeployResult")
    try:
        deploy = read_deploy_result(run)
    except Stop as stop:
        run.end(operation, stop.result, stop.reason)
        raise
    results["deploy"] = deploy["classification"]["deploy"]
    results["launch"] = deploy["classification"]["launch"]
    record["attemptId"] = deploy.get("attemptId")
    record["buildId"] = deploy.get("buildId")
    tag = (deploy.get("artifact") or {}).get("worldBuildTag")
    tag = tag if isinstance(tag, str) and TAG.fullmatch(tag) else None
    record["effectiveWorldBuildTag"] = tag
    try:
        stop_for_deploy(deploy)
    except Stop as stop:
        if deploy["classification"]["launch"] == "FAIL":
            results["clientReadiness"] = "FAIL"
        run.end(operation, stop.result, stop.reason)
        raise
    run.end(operation, "PASS")

    operation = run.begin("checkBridge")
    info = bridge_now(run)
    identity = bridge_identity(info)
    if not same_bridge(bridge_before, identity) or identity["slotPreconditions"] != bridge_before.get("slotPreconditions", False):
        run.end(operation, "UNKNOWN", "bridgeChanged")
        raise Stop("UNKNOWN", "bridgeChanged", "between prepare and the deploy result")
    # R1b: with slotPreconditions.v1 in Relaunch, the launch plan must be this deploy's when the
    # clients are fixed, and the operation revision read here must not move at any recheck. The
    # Bridge moves the revision before it writes a deploy's result, so nothing of this deploy
    # moves it after Unity has read the result. Without the feature (Bridge 0.11 and earlier)
    # nothing here changes R1a's procedure.
    guard = None
    if identity["slotPreconditions"] and bridge_before.get("mode") == "Relaunch":
        guard = {"bridgeInstanceId": identity["bridgeInstanceId"], "operationRevision": identity["operationRevision"],
                 "buildId": record.get("buildId")}
        record["guard"] = dict(guard, establishedAtUtc=iso())
    run.end(operation, "PASS")

    operation = run.begin("waitClientsReady")
    try:
        fixed = wait_ready(run, slots, cursor, bridge_before.get("mode"))
    except Stop as stop:
        results["clientReadiness"] = stop.result
        run.end(operation, stop.result, stop.reason, detail=stop.detail)
        raise
    results["clientReadiness"] = "PASS"
    record["ready"] = {str(k): v for k, v in fixed.items()}
    run.end(operation, "PASS")

    if guard:
        operation = run.begin("checkLaunchPlan")
        try:
            info = bridge_now(run)
            if not same_bridge(bridge_before, bridge_identity(info)):
                raise Stop("UNKNOWN", "bridgeChanged", "when the clients were ready")
            check_guard(info, guard, "when the clients were ready")
        except Stop as stop:
            results["clientReadiness"] = "UNKNOWN"
            run.end(operation, stop.result, stop.reason, detail=stop.detail)
            raise
        run.end(operation, "PASS")

    operation = run.begin("waitWorldBuild")
    parameters = (record.get("procedure") or {}).get("parameters")
    wait = parameters.get("worldBuildWaitSeconds", TAG_SECONDS) if isinstance(parameters, dict) else TAG_SECONDS
    if isinstance(wait, bool) or not isinstance(wait, (int, float)) or not 0 <= wait <= MAX_TAG_WAIT:
        wait = TAG_SECONDS  # run.json is this runner's own file; anything else falls back to the default
    match, reason = wait_world_build(run, slots, fixed, tag, wait)
    results["worldBuildMatch"] = match
    run.end(operation, match, reason)

    operation = run.begin("recheckReady")
    try:
        recheck(run, slots, fixed, bridge_before, "after readiness", guard)
    except Stop as stop:
        results["clientReadiness"] = "UNKNOWN"
        run.end(operation, stop.result, stop.reason, detail=stop.detail)
        raise
    run.end(operation, "PASS")

    end = parameters.get("endMarker") if isinstance(parameters, dict) else None
    if end is not None:
        if match == "FAIL":
            results["endMarker"] = "notReached"  # another build's tag: its end says nothing about this one
        else:
            operation = run.begin("waitEndMarker")
            try:
                word, why = wait_end_marker(run, slots, fixed, bridge_before, guard, end)
            except Stop as stop:
                if stop.result == "UNKNOWN" and stop.reason != "runDeadline":
                    results["clientReadiness"] = "UNKNOWN"
                run.end(operation, stop.result, stop.reason, detail=stop.detail)
                raise
            results["endMarker"] = word
            run.end(operation, word, why)

    operation = run.begin("collectEvidence")
    try:
        collect_evidence(run, slots, fixed)
    except Stop as stop:
        results["evidence"] = stop.result
        run.end(operation, stop.result, stop.reason, detail=stop.detail)
        raise
    results["evidence"] = "PASS"
    run.end(operation, "PASS")

    operation = run.begin("finalCheck")
    try:
        recheck(run, slots, fixed, bridge_before, "at the end", guard)
    except Stop as stop:
        run.end(operation, stop.result, stop.reason, detail=stop.detail)
        raise
    run.end(operation, "PASS")
    # Another build's tag means the evidence is of the wrong build; no tag at all stays UNKNOWN
    # in worldBuildMatch only, so worlds without markers can still be prepared and collected.
    if match == "FAIL":
        record["reason"] = "otherBuildTag"
        return "FAIL", fixed, guard
    if end is not None and results.get("endMarker") != "PASS":
        record["reason"] = (record.get("endMarker") or {}).get("reason") or "endMarkerNotSeen"
        return "UNKNOWN", fixed, guard
    return "PASS", fixed, guard


def add_evidence(run, path, **fields):
    size = os.path.getsize(path)
    run.evidence_bytes += size
    if run.evidence_bytes > MAX_EVIDENCE_BYTES:
        raise Stop("UNKNOWN", "evidenceTooLarge")
    run.record["evidence"].append(dict({"path": os.path.relpath(path, run.directory), "size": size,
                                        "sha256": sha256_of(path), "takenAtUtc": iso()}, **fields))
    run.save()


def check_windows_disk(info):
    free, exports = info.get("freeDiskBytes"), info.get("stateExportsBytes")
    if not isinstance(free, int) or not isinstance(exports, int):
        raise Stop("UNKNOWN", "windowsDiskUnknown")
    if free < MIN_WINDOWS_FREE or exports > MAX_WINDOWS_EXPORTS:
        raise Stop("UNKNOWN", "windowsDiskLow", "clear %%LOCALAPPDATA%%\\VRCRemoteTest\\state\\exports on Windows "
                                                "(free %d bytes, exports %d bytes)" % (free, exports))


def marker_cut(markers):
    records = markers.get("records") or []
    return max([r.get("seq", 0) for r in records if isinstance(r, dict) and isinstance(r.get("seq"), int)] or [0])


def save_markers(run, base, slot, want, markers, phase=None):
    """markers.json of one client generation and epoch under base/slot-<n>/g<gen>-e<epoch>/."""
    folder = os.path.join(base, "slot-%d" % slot, "g%d-e%d" % (want["clientGeneration"], want["observationEpoch"]))
    private_directory(os.path.dirname(folder))
    private_directory(folder)
    path = os.path.join(folder, "markers.json")
    write_new(path, json.dumps(markers, indent=2, ensure_ascii=True).encode("utf-8"))
    extra = {"phase": phase} if phase else {}
    add_evidence(run, path, kind="markers", slot=slot, clientGeneration=want["clientGeneration"],
                 observationEpoch=want["observationEpoch"], cutAtSeq=marker_cut(markers), **extra)
    return folder


def collect_slot(run, base, slot, want, phase=None):
    """One running client's markers and full log, of the generation and epoch in `want`.
    Returns the markers read."""
    check_mac_disk(run)
    markers = cli_json(run, "log", str(slot), "--markers", "--json", what="markers")
    header = markers.get("header") or {}
    if header.get("clientGeneration") != want["clientGeneration"] or header.get("observationEpoch") != want["observationEpoch"]:
        raise Stop("UNKNOWN", "markersChanged", "slot %d" % slot)
    folder = save_markers(run, base, slot, want, markers, phase)
    code, out, err = cli(run, "export-log", str(slot), folder, "--generation", str(want["clientGeneration"]),
                         "--epoch", str(want["observationEpoch"]), timeout=EXPORT_SECONDS)
    if code != 0:
        raise Stop("UNKNOWN", "exportFailed", "slot %d: %s" % (slot, err.strip()[-300:]))
    lines = [line.strip() for line in out.splitlines() if line.strip()]
    if not lines:
        raise Stop("UNKNOWN", "exportFailed", "slot %d: nothing exported" % slot)
    extra = {"phase": phase} if phase else {}
    for line in lines:
        real = os.path.realpath(line)
        if os.path.dirname(real) != os.path.realpath(folder) or os.path.islink(line) or not os.path.isfile(real):
            raise Stop("UNKNOWN", "exportFailed", "slot %d: unexpected output" % slot)
        add_evidence(run, real, kind="fullLog", slot=slot, clientGeneration=want["clientGeneration"],
                     observationEpoch=want["observationEpoch"],
                     note="a snapshot up to when it was taken, not necessarily the client's last line", **extra)
    return markers


def save_lists(run, base, clients, info, phase=None):
    extra = {"phase": phase} if phase else {}
    for name, value in (("clients.json", clients), ("bridge.json", info)):
        path = os.path.join(base, name)
        write_new(path, json.dumps(value, indent=2, ensure_ascii=True).encode("utf-8"))
        add_evidence(run, path, kind=name.split(".")[0], **extra)


def collect_evidence(run, slots, fixed):
    check_windows_disk(bridge_now(run))
    evidence = os.path.join(run.directory, "evidence")
    private_directory(evidence)
    for slot in slots:
        collect_slot(run, evidence, slot, fixed[slot])
    save_lists(run, evidence, clients_now(run), bridge_now(run))


# === leave-rejoin (R1b) ===

REGEX_CHILD = (
    "import json, re, sys\n"
    "pattern = re.compile(sys.argv[1])\n"
    "texts = json.loads(sys.stdin.buffer.read().decode('utf-8'))\n"
    "sys.stdout.write('1' if any(pattern.search(text) for text in texts) else '0')\n")


def regex_matches(pattern, texts):
    """Whether any of the (untrusted) lines matches the caller's --remaining-marker, decided in a
    child with an empty environment that is killed after REGEX_SECONDS, so a crafted line cannot
    hang the runner. None when it could not tell."""
    try:
        result = subprocess.run([sys.executable, "-I", "-c", REGEX_CHILD, pattern],
                                input=json.dumps(texts, ensure_ascii=True).encode("utf-8"), capture_output=True,
                                timeout=REGEX_SECONDS, env={}, start_new_session=True)
    except (OSError, subprocess.TimeoutExpired):
        return None
    if result.returncode != 0 or result.stdout not in (b"0", b"1"):
        return None
    return result.stdout == b"1"


def new_remaining_markers(run, remaining, fixed, cursor):
    """The marker lines after the cursor on the slots that stay, in their fixed generation and
    epoch, read through and with nothing dropped past the cursor. Cut, withheld or overlong
    lines never count. Returns (texts, complete)."""
    texts, complete = [], True
    for slot in remaining:
        want = fixed[slot]
        markers = cli_json(run, "log", str(slot), "--markers", "--json", what="markers")
        header = markers.get("header") if isinstance(markers.get("header"), dict) else {}
        if (header.get("clientGeneration") != want["clientGeneration"]
                or header.get("observationEpoch") != want["observationEpoch"] or header.get("catchingUp") is not False):
            complete = False
            continue
        dropped = header.get("droppedThroughSeq") or 0
        if not isinstance(dropped, int) or dropped > cursor[slot]:
            raise Stop("UNKNOWN", "markersDropped", "slot %d, after the close" % slot)
        for record in markers.get("records") or []:
            if (not isinstance(record, dict) or not isinstance(record.get("seq"), int) or record["seq"] <= cursor[slot]
                    or record.get("truncated") is not False or record.get("withheld") is not False):
                continue
            text = record.get("text")
            if not isinstance(text, str) or len(text) > MAX_MARKER_TEXT:
                complete = False
                continue
            texts.append(text)
    return texts, complete


# === deploy-collect --end-marker (docs/plans/end-marker-contract.md) ===

def end_marker_parameters(args, slots):
    """--end-marker, --end-slots and --end-wait from the command line only; None without --end-marker."""
    pattern = getattr(args, "end_marker", None)
    chosen = getattr(args, "end_slots", None)
    wait = getattr(args, "end_wait", None)
    if pattern is None:
        if chosen is not None or wait is not None:
            raise Usage("--end-slots and --end-wait go with --end-marker")
        return None
    if not valid_regex(pattern):
        raise Usage("--end-marker must be a regular expression of 1 to %d characters" % MAX_REGEX_CHARS)
    if chosen is None:
        end_slots = list(slots)
    else:
        try:
            end_slots = parse_slots(chosen)
        except Usage:
            raise Usage("--end-slots lists different slots from --slots, e.g. 0")
        if any(slot not in slots for slot in end_slots):
            raise Usage("--end-slots lists different slots from --slots, e.g. 0")
    wait = DEFAULT_END_WAIT if wait is None else wait
    if not MIN_END_WAIT <= wait <= MAX_END_WAIT:
        raise Usage("--end-wait takes %d to %d seconds" % (MIN_END_WAIT, MAX_END_WAIT))
    if not END_MARKER_VALIDATED and not getattr(args, "unvalidated", False):
        raise Usage("--end-marker has not been validated on real clients yet; add --unvalidated to use it anyway")
    return {"pattern": pattern, "slots": sorted(end_slots), "waitSeconds": wait, "unvalidated": not END_MARKER_VALIDATED}


def end_marker_ok(value, slots):
    """parameters.endMarker as prepare writes it, checked again before continue acts on it."""
    if not isinstance(value, dict) or set(value) != END_FIELDS or not isinstance(slots, list):
        return False
    chosen = value["slots"]
    return (valid_regex(value["pattern"]) and isinstance(chosen, list) and len(chosen) >= 1
            and all(is_count(slot, 0, 3) and slot in slots for slot in chosen) and chosen == sorted(set(chosen))
            and is_count(value["waitSeconds"], MIN_END_WAIT, MAX_END_WAIT)
            and type(value["unvalidated"]) is bool and (END_MARKER_VALIDATED or value["unvalidated"] is True))


FIRST_MATCH_CHILD = (
    "import resource\n"
    "resource.setrlimit(resource.RLIMIT_CPU, (3, 3))\n"  # ends itself even when the runner is gone
    "import json, re, sys\n"
    "pattern = re.compile(sys.argv[1])\n"
    "texts = json.loads(sys.stdin.buffer.read().decode('utf-8'))\n"
    "sys.stdout.write(str(next((i for i, text in enumerate(texts) if pattern.search(text)), -1)))\n")


def regex_first(pattern, texts):
    """The index of the first (untrusted) line that matches the caller's --end-marker, -1 for
    none, or None when it could not tell: one child per call, with an empty environment, killed
    after REGEX_SECONDS and limited to 3 s of CPU by itself."""
    try:
        result = subprocess.run([sys.executable, "-I", "-c", FIRST_MATCH_CHILD, pattern],
                                input=json.dumps(texts, ensure_ascii=True).encode("utf-8"), capture_output=True,
                                timeout=REGEX_SECONDS, env={}, start_new_session=True)
    except (OSError, subprocess.TimeoutExpired):
        return None
    if result.returncode != 0 or not re.fullmatch(rb"-1|0|[1-9][0-9]{0,4}", result.stdout or b""):
        return None
    index = int(result.stdout)
    return index if index < len(texts) else None


def end_marker_round(run, slot, want, through, pattern, timeout):
    """One look at one end slot's markers after `through`. Returns (word, seq, markers):
    ("match", the matching line's seq, the markers read), ("none", how far the lines are settled,
    None), ("incomplete" | "dropped" | "undecided", None, None). Cut, withheld or overlong lines
    never match. The newest record can still grow (the Bridge publishes an open record), so it is
    looked at again in the next round."""
    markers = cli_json(run, "log", str(slot), "--markers", "--json", what="markers", timeout=timeout)
    header = markers.get("header") if isinstance(markers.get("header"), dict) else {}
    records = markers.get("records")
    if (header.get("clientGeneration") != want["clientGeneration"]
            or header.get("observationEpoch") != want["observationEpoch"] or header.get("catchingUp") is not False
            or not isinstance(records, list)):
        return "incomplete", None, None
    dropped = header.get("droppedThroughSeq") or 0
    if not is_count(dropped):
        return "incomplete", None, None
    if dropped > through:
        return "dropped", None, None
    lines, seqs = [], []
    for record in records:
        if not isinstance(record, dict) or not is_count(record.get("seq")) or record["seq"] <= through:
            continue
        seqs.append(record["seq"])
        text = record.get("text")
        if (record.get("truncated") is not False or record.get("withheld") is not False
                or not isinstance(text, str) or len(text) > MAX_MARKER_TEXT):
            continue
        lines.append((record["seq"], text))
    settled = max([seq for seq in seqs if seq < max(seqs)] or [through]) if seqs else through
    lines.sort(key=lambda line: line[0])
    if not lines:
        return "none", settled, None
    index = regex_first(pattern, [text for _, text in lines])
    if index is None:
        return "undecided", None, None
    if index >= 0:
        return "match", lines[index][0], markers
    return "none", settled, None


def wait_end_marker(run, slots, fixed, bridge_before, guard, end):
    """Waits until every end slot logged a marker matching the pattern in its current join,
    checking all declared slots each round as recheck does. A line found is taken only once a
    later recheck shows the client list read through it (the Bridge publishes markers before the
    list, so until then the line could be of a later join); its markers are saved as evidence
    then. Returns (PASS, None) or (UNKNOWN, endMarkerTimeout | markersDropped |
    endMarkerUndecided), after which the evidence is still collected; raises Stop when the
    clients or the Bridge changed, the observation stayed incomplete, on a cancel or the run's
    deadline."""
    state = {"pattern": end["pattern"], "slots": end["slots"], "waitSeconds": end["waitSeconds"],
             "startedAtUtc": iso(), "endedAtUtc": None, "matched": {}, "incompleteRounds": 0, "reason": None}
    run.record["endMarker"] = state
    run.save()
    base = os.path.join(run.directory, "evidence", "end-marker")
    through, pending = {}, {}
    for slot in end["slots"]:
        want = fixed[slot]
        through[slot] = want["joinAttemptSeq"] if want["joinAttemptSeq"] is not None else want["joinedSeq"]
    finish_at = time.monotonic() + min(end["waitSeconds"], END_WAIT_CAP)
    incomplete_since = None

    def done(word, reason, **fields):
        state.update(fields, endedAtUtc=iso(), reason=reason)
        run.save()
        return word, reason

    while True:
        run.check()
        looked = time.monotonic()
        left = OBSERVATION_GRACE_SECONDS - (looked - incomplete_since) if incomplete_since else OBSERVATION_GRACE_SECONDS
        short = max(0.5, min(MANUAL_READ_SECONDS, left))
        complete, outcome = True, None
        try:
            _, clients = recheck(run, slots, fixed, bridge_before, "while waiting for the end marker", guard, timeout=short)
            for slot, (seq, markers) in list(pending.items()):
                covered = (entry_of(clients, slot) or {}).get("observedThroughSeq")
                if is_count(covered) and covered >= seq:
                    private_directory(os.path.join(run.directory, "evidence"))
                    private_directory(base)
                    folder = save_markers(run, base, slot, fixed[slot], markers, phase="endMarker")
                    state["matched"][str(slot)] = {"seq": seq, "seenAtUtc": iso(),
                                                   "evidence": os.path.relpath(os.path.join(folder, "markers.json"),
                                                                               run.directory)}
                    del pending[slot]
                    run.save()
                else:
                    complete = False
            for slot in end["slots"]:
                if str(slot) in state["matched"] or slot in pending:
                    continue
                found, seq, markers = end_marker_round(run, slot, fixed[slot], through[slot], end["pattern"], short)
                if found == "match":
                    pending[slot] = (seq, markers)
                elif found == "none":
                    through[slot] = seq
                elif found == "incomplete":
                    complete = False
                else:
                    outcome = found
                    break
            incomplete_since = None
        except Stop as stop:
            if stop.result != "UNKNOWN" or stop.reason not in INCOMPLETE_REASONS + ("markersUnavailable", "markersInvalid"):
                raise
            complete = False
            incomplete_since = incomplete_since or looked
            if time.monotonic() - incomplete_since >= OBSERVATION_GRACE_SECONDS:
                raise Stop("UNKNOWN", "observationIncomplete",
                           "while waiting for the end marker: %s" % (stop.detail or stop.reason))
        if outcome == "dropped":
            return done("UNKNOWN", "markersDropped")
        if outcome == "undecided":
            return done("UNKNOWN", "endMarkerUndecided")
        if len(state["matched"]) == len(end["slots"]):
            return done("PASS", None)
        if not complete:
            state["incompleteRounds"] += 1
        missing = [slot for slot in end["slots"] if str(slot) not in state["matched"]]
        if time.monotonic() >= finish_at:
            return done("UNKNOWN", "endMarkerTimeout", missing=missing, lastRoundComplete=complete,
                        unconfirmed=sorted(pending))
        run.progress("wait", "waiting for the end marker", missing=missing)
        # A line waiting for the list to catch up is looked at again soon.
        run.wait(CHECK_SECONDS if pending else END_POLL_SECONDS)


def expect_from(info, generation):
    """The precondition for one close or launch (docs/plans/r1b-contract.md §1.3), from the
    /bridge just read and the client generation this run fixed. Checked before it is written."""
    batch = current_batch(info) or {}
    expect = {"bridgeInstanceId": info.get("bridgeInstanceId"), "hostGeneration": info.get("hostGeneration"),
              "activeConfigEtag": info.get("activeConfigEtag"), "savedConfigEtag": info.get("savedConfigEtag"),
              "operationRevision": info.get("operationRevision"), "buildId": batch.get("buildId"),
              "clientGeneration": generation}
    problems = []
    if not (isinstance(expect["bridgeInstanceId"], str) and INSTANCE_ID.fullmatch(expect["bridgeInstanceId"])):
        problems.append("bridgeInstanceId")
    if not is_count(expect["hostGeneration"], 1):
        problems.append("hostGeneration")
    for key in ("activeConfigEtag", "savedConfigEtag"):
        if not (expect[key] is None or (isinstance(expect[key], str) and len(expect[key]) <= 128)):
            problems.append(key)
    if not is_count(expect["operationRevision"]):
        problems.append("operationRevision")
    if not (expect["buildId"] is None or (isinstance(expect["buildId"], str) and EXPECT_BUILD_ID.fullmatch(expect["buildId"]))):
        problems.append("buildId")
    if not is_count(generation, 1):
        problems.append("clientGeneration")
    if problems:
        raise Stop("UNKNOWN", "expectInvalid", "cannot state the precondition: " + ", ".join(problems))
    return expect


def answer_summary(answer):
    """The scalar fields of a close/launch answer (and a 412's), bounded, for run.json."""
    def scalar(value):
        if value is None or isinstance(value, (bool, int)):
            return value
        return str(value)[:128]
    summary = {}
    for key in ("slot", "processId", "profile", "closed", "notConfirmed", "recordUpdated", "launched", "recorded",
                "operationRevision", "bridgeInstanceId", "operationId", "clientGeneration", "error"):
        if key in answer:
            summary[key] = scalar(answer[key])
    if isinstance(answer.get("mismatched"), list):
        summary["mismatched"] = [str(item)[:64] for item in answer["mismatched"][:7]]
    if isinstance(answer.get("current"), dict):
        summary["current"] = {str(k)[:64]: scalar(v) for k, v in list(answer["current"].items())[:7]}
    return summary


def reread(run, entry):
    """After a close or launch that did not end as expected: what the Bridge shows now, into
    run.json. Never followed by another request."""
    observed = {"atUtc": iso()}
    for name, read in (("bridge", bridge_now), ("clients", clients_now)):
        try:
            observed[name] = read(run)
        except Stop as stop:
            observed[name + "Unavailable"] = stop.reason
    entry["reread"] = observed
    run.save()


def send_slot_operation(run, state, kind, slot, expect):
    """One preconditioned close or launch, sent once: the intent (operationId, expect) is in
    run.json and expect-<kind>.json before the request. Only exit 0 with the Bridge's answer to
    exactly this request (operationRevision = expect + 1; close: the expected generation; launch:
    a new one) is accepted. Anything else, a lost answer or a cancel is recorded with a re-read of
    /bridge and /clients and stops the procedure: never sent again. Returns (answer, answered)."""
    operation_id = "%s-%s" % (run.record["runId"], kind)
    if not OPERATION_ID.fullmatch(operation_id):
        raise Stop("UNKNOWN", "operationIdInvalid")
    path = os.path.join(run.directory, "expect-%s.json" % kind)
    entry = state[kind] = {"operationId": operation_id, "expect": expect, "expectFile": os.path.basename(path),
                           "outcome": "notSent"}
    write_new(path, json.dumps(expect, indent=2, ensure_ascii=True).encode("utf-8"))
    run.check()
    # From here the request may reach the Bridge: a crash or a cancel leaves this on record.
    entry["outcome"] = "unknown"
    state["clientMayBeLeftClosed"] = True
    timeout = CLOSE_CLI_SECONDS if kind == "close" else LAUNCH_CLI_SECONDS
    with step(run, kind + "Client", slot=slot, operationId=operation_id):
        try:
            code, out, err = supervise(run, ["/bin/bash", CLI, kind + "-client", str(slot), "--expect", "@" + path,
                                             "--operation-id", operation_id, "--json"], timeout, writes=True)
        except Stop as stop:
            entry["note"] = "the request may have reached the Bridge; it was not sent again"
            if stop.result != "CANCELED":
                reread(run, entry)
            raise Stop(stop.result, kind + ("Canceled" if stop.result == "CANCELED" else "NoAnswer"),
                       "slot %d (%s): whether the Bridge did it is unknown; it was not sent again" % (slot, stop.reason))
        answered = utc_now()
        entry.update({"answeredAtUtc": iso(answered), "exitCode": code})
        try:
            answer = json.loads(out) if out.strip() else None
        except (ValueError, RecursionError):
            answer = None
        if isinstance(answer, dict):
            entry["answer"] = answer_summary(answer)
        if code != 0:
            precondition = code == 1 and isinstance(answer, dict) and answer.get("error") == "preconditionFailed"
            if precondition:
                reason, entry["outcome"] = kind + "PreconditionFailed", "preconditionFailed"
            else:
                reason = kind + {1: "NotDone", 2: "Refused", 3: "Unavailable"}.get(code, "Failed")
                entry["outcome"] = "refused" if code == 2 else "unknown"
            if kind == "close" and (precondition or code == 2):
                state["clientMayBeLeftClosed"] = False  # refused before anything was done
            reread(run, entry)
            raise Stop("UNKNOWN", reason, "slot %d: vrcrt.sh exit %d: %s; not sent again" % (slot, code, err.strip()[-300:]))
        problems = []
        if not isinstance(answer, dict):
            problems.append("no JSON answer")
        else:
            revision, generation = answer.get("operationRevision"), answer.get("clientGeneration")
            if not is_count(answer.get("slot"), 0, 3) or answer.get("slot") != slot:
                problems.append("slot")
            if not is_count(revision) or revision != expect["operationRevision"] + 1:
                problems.append("operationRevision %s, expected %d" % (revision, expect["operationRevision"] + 1))
            if answer.get("bridgeInstanceId") != expect["bridgeInstanceId"]:
                problems.append("bridgeInstanceId")
            if answer.get("operationId") != operation_id:
                problems.append("operationId")
            if kind == "close" and (not is_count(generation, 1) or generation != expect["clientGeneration"]):
                problems.append("closed generation %s, expected %d" % (generation, expect["clientGeneration"]))
            if kind == "launch" and (not is_count(generation, 1) or generation == expect["clientGeneration"]):
                problems.append("launched generation %s is not a new one" % generation)
        if problems:
            entry["outcome"] = "unexpectedAnswer"
            reread(run, entry)
            raise Stop("UNKNOWN", kind + "AnswerUnexpected", "slot %d: %s; not sent again" % (slot, "; ".join(problems)))
        entry["outcome"] = "done"
        if kind == "launch":
            state["clientMayBeLeftClosed"] = False
    return answer, answered


def observe_left(run, target, remaining, fixed, closed_generation, answered, guard, bridge_before, when):
    """One look after the close: the same Bridge, launch plan and revision (no operation but this
    run's close), a list updated after the close's answer, the slots that stay exactly as fixed,
    and the target slot's closed client. Returns (left, reasons, complete, /bridge, /clients)."""
    run.check()
    info = bridge_now(run)
    if not same_bridge(bridge_before, bridge_identity(info)):
        raise Stop("UNKNOWN", "bridgeChanged", when)
    check_guard(info, guard, when)
    clients = clients_now(run)
    reasons, complete = {}, list_fresh(clients)
    if not complete:
        reasons["observation"] = "stale"
    updated = parse_utc(clients.get("updatedAtUtc"))
    if updated is None or updated <= answered:
        reasons["list"] = "notUpdatedSinceClose"  # the Bridge's list can be about 10 s behind
        complete = False
    for slot in remaining:
        check_unchanged(entry_of(clients, slot), fixed[slot], slot, when)
    entry, left = entry_of(clients, target), False
    if entry is None:
        reasons[str(target)] = "noClient"
        complete = False
    elif entry.get("clientGeneration") != closed_generation:
        raise Stop("UNKNOWN", "unexpectedChange", "slot %d holds generation %s %s" % (target, entry.get("clientGeneration"), when))
    elif entry.get("running") is False:
        left = True
    elif entry.get("running") is True:
        reasons[str(target)] = "stillRunning"
    else:
        reasons[str(target)] = "runningUnknown"
        complete = False
    return left, reasons, complete, info, clients


def wait_left(run, target, remaining, fixed, closed_generation, answered, guard, bridge_before, pattern, cursor, seconds):
    """Until the closed client shows not running and, with --remaining-marker, a slot that stays
    has logged a matching marker after the cursor. FAIL only when every look was complete."""
    end = time.monotonic() + seconds
    seen = pattern is None
    when = "while slot %d leaves" % target
    while True:
        left, reasons, complete, info, clients = observe_left(run, target, remaining, fixed, closed_generation,
                                                              answered, guard, bridge_before, when)
        if not seen:
            texts, readable = new_remaining_markers(run, remaining, fixed, cursor)
            matched = regex_matches(pattern, texts) if texts else False
            seen = matched is True
            if not seen:
                readable = readable and matched is not None
                reasons["remainingMarker"] = "notSeen" if readable else "notReadable"
                complete = complete and readable
        if left and seen and complete:
            return info, clients
        if time.monotonic() >= end:
            detail = ", ".join("%s=%s" % item for item in sorted(reasons.items()))
            if not complete:
                raise Stop("UNKNOWN", "leaveNotObserved", detail)
            raise Stop("FAIL", "remainingMarkerMissing" if left else "targetNotClosed", detail)
        run.progress("wait", "waiting for slot %d to leave" % target, reasons=reasons)
        run.wait(POLL_SECONDS)


def wait_rejoined(run, target, remaining, fixed, closed_generation, new_generation, guard, bridge_before, seconds):
    """Until the launched generation is ready in the target slot (joined after its start, not in
    the error world, logged in) in the instance of the slots that stayed, which stay as fixed."""
    end = time.monotonic() + seconds
    instance = fixed[remaining[0]]["instance"]
    when = "while slot %d rejoins" % target
    while True:
        run.check()
        info = bridge_now(run)
        if not same_bridge(bridge_before, bridge_identity(info)):
            raise Stop("UNKNOWN", "bridgeChanged", when)
        check_guard(info, guard, when)
        clients = clients_now(run)
        reasons, complete = {}, list_fresh(clients)
        if not complete:
            reasons["observation"] = "stale"
        for slot in remaining:
            check_unchanged(entry_of(clients, slot), fixed[slot], slot, when)
        entry = entry_of(clients, target)
        generation = entry.get("clientGeneration") if entry else None
        if entry is None:
            reasons[str(target)] = "noClient"
            complete = False
        elif generation not in (closed_generation, new_generation):
            raise Stop("UNKNOWN", "unexpectedChange", "slot %d holds generation %s, not the launched %d"
                       % (target, generation, new_generation))
        elif generation == closed_generation:
            reasons[str(target)] = "launchNotListedYet"
        else:
            state = slot_state(entry, {"clientGeneration": closed_generation}, "Relaunch")
            if state[0] == "stop":
                raise Stop(state[1], state[2], "slot %d after the launch" % target)
            if state[0] == "wait":
                reasons[str(target)] = state[1]
                complete = complete and not state[2]
            elif state[1]["instance"] != instance:
                reasons[str(target)] = "otherInstance"
            elif complete:
                return state[1], info, clients
        if time.monotonic() >= end:
            detail = ", ".join("%s=%s" % item for item in sorted(reasons.items()))
            raise Stop("FAIL" if complete else "UNKNOWN", "notRejoined", detail)
        run.progress("wait", "waiting for slot %d to rejoin" % target, reasons=reasons)
        run.wait(POLL_SECONDS)


def previous_markers(run, slot, want):
    """The closed client's markers, when the Bridge still shows that generation read through;
    None otherwise (the evidence is then missing and leave-rejoin cannot PASS)."""
    try:
        markers = cli_json(run, "log", str(slot), "--markers", "--json", what="markers")
    except Stop as stop:
        if stop.reason in ("markersUnavailable", "markersInvalid"):
            return None
        raise
    header = markers.get("header") if isinstance(markers.get("header"), dict) else {}
    if (header.get("clientGeneration") != want["clientGeneration"]
            or header.get("observationEpoch") != want["observationEpoch"] or header.get("catchingUp") is not False):
        return None
    return markers


def phase_folder(run, state, key, name, under=("leave-rejoin",)):
    """evidence/<under...>/<name>/ (evidence/leave-rejoin/<name>/ for leave-rejoin), recorded in
    the state's evidence."""
    base = os.path.join(run.directory, "evidence")
    private_directory(base)
    for part in under:
        base = os.path.join(base, part)
        private_directory(base)
    folder = os.path.join(base, name)
    private_directory(folder)
    state["evidence"][key] = os.path.relpath(folder, run.directory)
    return folder


def leave_rejoin(run, fixed, guard):
    """r1-automation.md §5.4 steps 1-8, after deploy-collect passed in the same continue."""
    record = run.record
    parameters = record["procedure"]["parameters"]
    target = parameters["targetSlot"]
    remaining = [slot for slot in record["slots"] if slot != target]
    state = record["leaveRejoin"] = {"state": "started", "targetSlot": target, "remainingSlots": remaining,
                                     "remainingMarker": parameters.get("remainingMarker"),
                                     "clientMayBeLeftClosed": False, "evidence": {}, "evidenceGaps": []}
    try:
        word, reason, _, _ = leave_rejoin_steps(run, state, fixed, guard, target, remaining, parameters)
    except (Stop, KeyboardInterrupt) as error:
        record["results"]["leaveRejoin"] = error.result if isinstance(error, Stop) else "CANCELED"
        state["state"] = "stopped"
        if state["clientMayBeLeftClosed"]:
            state["note"] = ("slot %d may be left closed (or a client may have started): check `vrcrt.sh status` and "
                             "use Close/Launch to go on; nothing was sent again" % target)
            if isinstance(error, Stop) and error.result != "CANCELED":
                reread(run, state)  # what the Bridge shows where the procedure stopped
        raise
    record["results"]["leaveRejoin"] = word
    state["state"] = "finished"
    if reason:
        record["reason"] = reason
    return word


def leave_rejoin_steps(run, state, fixed, guard, target, remaining, parameters, under=("leave-rejoin",)):
    """Returns (word, reason, final fixed, final guard): the slots as they were, the target as its
    new generation, and the guard at the revision of this run's own launch."""
    record = run.record
    slots = record["slots"]
    bridge_before = record["bridgeBefore"]
    pattern = parameters.get("remainingMarker")

    # 1. The feature, the launch plan and every slot as fixed; nothing is sent without them.
    with step(run, "leaveRejoinPreconditions"):
        if guard is None:
            raise Stop("UNKNOWN", "slotPreconditionsUnsupported",
                       "the Bridge does not take preconditioned close/launch in Relaunch; nothing was sent")
        info, clients = recheck(run, slots, fixed, bridge_before, "before the close", guard)
        if target not in ((current_batch(info) or {}).get("launchedSlots") or []):
            raise Stop("UNKNOWN", "targetNotRelaunchable", "slot %d is not in the Bridge's launch plan; nothing was sent" % target)
        check_windows_disk(info)

    # 2. Before the close: every slot's markers and full log, then /bridge and /clients once more;
    # the precondition comes from that /bridge, the marker cursor from those snapshots.
    with step(run, "snapshotBeforeClose"):
        folder = phase_folder(run, state, "beforeClose", "before-close", under)
        markers = {slot: collect_slot(run, folder, slot, fixed[slot], "beforeClose") for slot in slots}
        info, clients = recheck(run, slots, fixed, bridge_before, "after the snapshot before the close", guard)
        save_lists(run, folder, clients, info, "beforeClose")
        cursor = {}
        for slot in remaining:
            if (markers[slot].get("header") or {}).get("catchingUp") is not False:
                raise Stop("UNKNOWN", "observationIncomplete", "slot %d markers before the close" % slot)
            through = (entry_of(clients, slot) or {}).get("observedThroughSeq")
            cursor[slot] = max(marker_cut(markers[slot]), through if is_count(through) else 0)
        state["markerCursor"] = {str(slot): seq for slot, seq in cursor.items()}
        closed_generation = fixed[target]["clientGeneration"]
        expect = expect_from(info, closed_generation)

    # 3. Close, once.
    answer, answered = send_slot_operation(run, state, "close", target, expect)
    state["closedGeneration"] = closed_generation
    guard = dict(guard, operationRevision=answer["operationRevision"])

    # 4. The target has left; the others stay; the caller's marker, if any, on a slot that stays.
    with step(run, "waitTargetLeft"):
        info, clients = wait_left(run, target, remaining, fixed, closed_generation, answered, guard, bridge_before,
                                  pattern, cursor, procedure_seconds(parameters, "leaveWaitSeconds", LEAVE_SECONDS))

    # 5. After the close: the others' markers, the closed client's last markers, the lists.
    with step(run, "snapshotAfterClose"):
        folder = phase_folder(run, state, "afterClose", "after-close", under)
        for slot in remaining:
            markers = cli_json(run, "log", str(slot), "--markers", "--json", what="markers")
            header = markers.get("header") if isinstance(markers.get("header"), dict) else {}
            if (header.get("clientGeneration") != fixed[slot]["clientGeneration"]
                    or header.get("observationEpoch") != fixed[slot]["observationEpoch"]):
                raise Stop("UNKNOWN", "markersChanged", "slot %d after the close" % slot)
            save_markers(run, folder, slot, fixed[slot], markers, "afterClose")
        previous = previous_markers(run, target, fixed[target])
        if previous is None:
            state["evidenceGaps"].append("slot %d: the closed client's markers after the close" % target)
        else:
            save_markers(run, folder, target, fixed[target], previous, "afterClose")
        save_lists(run, folder, clients, info, "afterClose")

    # 6. Launch, once, from what the Bridge shows right now.
    with step(run, "checkBeforeLaunch"):
        left, reasons, complete, info, clients = observe_left(run, target, remaining, fixed, closed_generation, answered,
                                                              guard, bridge_before, "before the launch")
        if not (left and complete):
            raise Stop("UNKNOWN", "observationIncomplete" if not complete else "unexpectedChange",
                       "before the launch: " + ", ".join("%s=%s" % item for item in sorted(reasons.items())))
        expect = expect_from(info, closed_generation)
    answer, _ = send_slot_operation(run, state, "launch", target, expect)
    new_generation = answer["clientGeneration"]
    state["newGeneration"] = new_generation
    guard = dict(guard, operationRevision=answer["operationRevision"])

    # 7. The new generation ready in the same instance (and showing this build's tag, when it did).
    with step(run, "waitTargetRejoined"):
        rejoined, info, clients = wait_rejoined(run, target, remaining, fixed, closed_generation, new_generation, guard,
                                                bridge_before, procedure_seconds(parameters, "rejoinWaitSeconds", REJOIN_SECONDS))
    final = dict(fixed)
    final[target] = rejoined
    state["rejoined"] = rejoined
    # Whenever these bytes carry a tag, the new generation must not show another one (FAIL). It
    # must show this one only when the first join did (a world that logs no tag stays as it was):
    # then the wait is the run's; otherwise the markers are looked at once.
    match = None
    tag = record.get("effectiveWorldBuildTag")
    if tag:
        shown_before = record["results"].get("worldBuildMatch") == "PASS"
        with step(run, "waitWorldBuildRejoined") as operation:
            wait = parameters.get("worldBuildWaitSeconds", TAG_SECONDS)
            if isinstance(wait, bool) or not isinstance(wait, (int, float)) or not 0 <= wait <= MAX_TAG_WAIT:
                wait = TAG_SECONDS
            match, why = wait_world_build(run, [target], final, tag, wait if shown_before else 0)
            if match == "UNKNOWN" and not shown_before:
                match, why = None, "noTagBefore"
            state["worldBuildMatch"] = match
            run.end(operation, match or "UNKNOWN", why)

    # 8. The new generation's evidence apart from the earlier ones, then every slot once more.
    with step(run, "recheckRejoined"):
        info, clients = recheck(run, record["slots"], final, bridge_before, "after the rejoin", guard)
        check_windows_disk(info)
    with step(run, "snapshotAfterRejoin"):
        folder = phase_folder(run, state, "afterRejoin", "after-rejoin", under)
        for slot in slots:
            collect_slot(run, folder, slot, final[slot], "afterRejoin")
        save_lists(run, folder, clients_now(run), bridge_now(run), "afterRejoin")
    with step(run, "leaveRejoinFinalCheck"):
        recheck(run, slots, final, bridge_before, "at the end of leave-rejoin", guard)
    if match == "FAIL":
        return "FAIL", "otherBuildTagAfterRejoin", final, guard
    if match == "UNKNOWN":
        return "UNKNOWN", "buildTagAfterRejoinUnknown", final, guard
    if state["evidenceGaps"]:
        return "UNKNOWN", "evidenceIncomplete", final, guard
    return "PASS", None, final, guard


# === manual-steps (R3, docs/plans/r3-contract.md) ===

CHECKLIST_SCHEMA = "vrcrt.checklist"
WAITING_SCHEMA = "vrcrt.waiting"
ANSWER_SCHEMA = "vrcrt.answer"
ITEM_ID = re.compile(r"[a-z0-9][a-z0-9_-]{0,31}")
ANSWERS = ("ok", "ng", "skip")
ITEM_STATES = ("notAsked", "waiting", "answered", "unanswered", "interrupted")
# Characters shown as \uXXXX: C0 but newline, DEL, C1, line/paragraph separators, bidi controls.
HIDDEN = re.compile(r"[\x00-\x09\x0b-\x1f\x7f-\x9f\u2028\u2029\u202a-\u202e\u2066-\u2069]")


def strict_json(data):
    """JSON with no duplicate names and no NaN/Infinity, from bytes that must be UTF-8."""
    def pairs(items):
        names = [name for name, _ in items]
        if len(names) != len(set(names)):
            raise ValueError("a name appears twice")
        return dict(items)

    def constant(name):
        raise ValueError("%s is not JSON" % name)

    return json.loads(data.decode("utf-8"), object_pairs_hook=pairs, parse_constant=constant)


def read_owned(path, limit):
    """The bytes of an ordinary file of this user that no one else may write, read without
    following a link and without blocking on a FIFO: checked on the descriptor it reads."""
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    try:
        info = os.fstat(descriptor)
        if not stat.S_ISREG(info.st_mode):
            raise ValueError("%s is not an ordinary file" % os.path.basename(path))
        if info.st_uid != os.getuid() or info.st_mode & 0o022:
            raise ValueError("%s is not this user's own file" % os.path.basename(path))
        chunks, size = [], 0
        while True:
            chunk = os.read(descriptor, min(65536, limit + 1 - size))
            if not chunk:
                break
            chunks.append(chunk)
            size += len(chunk)
            if size > limit:
                raise ValueError("%s is larger than %d bytes" % (os.path.basename(path), limit))
    finally:
        os.close(descriptor)
    return b"".join(chunks)


def plain_text(value, low, high):
    """A string of low..high code points with no surrogates (JSON escapes can make lone ones)."""
    return (isinstance(value, str) and low <= len(value) <= high
            and not any("\ud800" <= ch <= "\udfff" for ch in value))


def check_checklist(data):
    """The checklist's bytes as r3-contract.md §1 allows them; returns (title, items)."""
    if len(data) > MAX_CHECKLIST_BYTES:
        raise ValueError("the checklist is larger than %d bytes" % MAX_CHECKLIST_BYTES)
    try:
        value = strict_json(data)
    except (ValueError, UnicodeDecodeError, RecursionError) as error:
        raise ValueError("the checklist is not strict UTF-8 JSON: %s" % error)
    if not isinstance(value, dict) or set(value) != {"schema", "schemaVersion", "title", "items"}:
        raise ValueError("the checklist has exactly schema, schemaVersion, title and items")
    if value["schema"] != CHECKLIST_SCHEMA or type(value["schemaVersion"]) is not int or value["schemaVersion"] != 1:
        raise ValueError("the checklist is not %s version 1" % CHECKLIST_SCHEMA)
    if not plain_text(value["title"], 1, MAX_TITLE_CHARS):
        raise ValueError("the checklist title has 1 to %d characters" % MAX_TITLE_CHARS)
    items = value["items"]
    if not isinstance(items, list) or not 1 <= len(items) <= MAX_ITEMS:
        raise ValueError("the checklist has 1 to %d items" % MAX_ITEMS)
    seen = set()
    for index, item in enumerate(items, 1):
        if not isinstance(item, dict) or set(item) != {"id", "text"}:
            raise ValueError("item %d has exactly id and text" % index)
        if not isinstance(item["id"], str) or not ITEM_ID.fullmatch(item["id"]) or item["id"] in seen:
            raise ValueError("item %d's id must match [a-z0-9][a-z0-9_-]{0,31} and be unique" % index)
        seen.add(item["id"])
        if not plain_text(item["text"], 1, MAX_ITEM_CHARS):
            raise ValueError("item %d's text has 1 to %d characters" % (index, MAX_ITEM_CHARS))
    return value["title"], items


def visible(text):
    """Display only: what could hide or reorder text becomes \\uXXXX."""
    return HIDDEN.sub(lambda match: "\\u%04x" % ord(match.group(0)), text)


def load_checklist(path):
    try:
        data = read_owned(path, MAX_CHECKLIST_BYTES)
        title, items = check_checklist(data)
    except (OSError, ValueError) as error:
        raise Usage("--checklist: %s" % error)
    return data, title, items


class AnswerLock:
    """answer.lock in the run folder: the runner's opening, reading and closing of a wait and
    `answer`'s publishing take turns (flock; the kernel drops it when its holder dies)."""

    def __init__(self, directory):
        self.path = os.path.join(directory, "answer.lock")
        self._descriptor = None

    def __enter__(self):
        self._descriptor = os.open(self.path, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
        fcntl.flock(self._descriptor, fcntl.LOCK_EX)
        return self

    def __exit__(self, *exc):
        os.close(self._descriptor)
        self._descriptor = None


def waiting_path(directory):
    return os.path.join(directory, "waiting.json")


def answer_path(directory, wait_id):
    return os.path.join(directory, "answer-%s.json" % wait_id)


def read_waiting(directory):
    try:
        value = strict_json(read_owned(waiting_path(directory), MAX_WAITING_BYTES))
    except FileNotFoundError:
        return None
    except (OSError, ValueError, UnicodeDecodeError, RecursionError):
        return {"unreadable": True}
    return value if isinstance(value, dict) else {"unreadable": True}


def open_wait(run, index, count, item_id, deadline):
    """Under answer.lock: a new wait for one item. Returns the waitId."""
    wait_id = secrets.token_hex(16)
    with AnswerLock(run.directory):
        write_json(waiting_path(run.directory), {
            "schema": WAITING_SCHEMA, "schemaVersion": 1, "runId": run.record["runId"], "waiting": True,
            "waitId": wait_id, "index": index, "count": count, "itemId": item_id,
            "startedAtUtc": iso(), "deadlineUtc": iso(deadline)})
    return wait_id


def close_wait_locked(run, wait_id):
    write_json(waiting_path(run.directory), {"schema": WAITING_SCHEMA, "schemaVersion": 1,
                                             "runId": run.record["runId"], "waiting": False,
                                             "closedAtUtc": iso(), "lastWaitId": wait_id})


def take_answer_locked(run, wait_id, item_id):
    """With answer.lock held: the published answer to this wait, None when there is none yet.
    Anything there that is not exactly this wait's answer stops the run (invalidAnswer)."""
    path = answer_path(run.directory, wait_id)
    if not os.path.lexists(path):
        return None
    try:
        value = strict_json(read_owned(path, MAX_ANSWER_BYTES))
    except (OSError, ValueError, UnicodeDecodeError, RecursionError) as error:
        raise Stop("UNKNOWN", "invalidAnswer", str(error)[:300])
    keys = {"schema", "schemaVersion", "runId", "waitId", "itemId", "answer", "memo", "by", "writtenAtUtc"}
    if (not isinstance(value, dict) or set(value) != keys or value["schema"] != ANSWER_SCHEMA
            or type(value["schemaVersion"]) is not int or value["schemaVersion"] != 1
            or value["runId"] != run.record["runId"] or value["waitId"] != wait_id or value["itemId"] != item_id
            or value["answer"] not in ANSWERS or value["by"] not in ("unity", "cli")
            or not plain_text(value["memo"], 0, MAX_MEMO_CHARS) or parse_utc(value["writtenAtUtc"]) is None):
        raise Stop("UNKNOWN", "invalidAnswer", "the answer to %s is not this wait's" % item_id)
    return value


def clean_memo(text):
    return "".join(ch for ch in text if ch in "\n\t" or not (ch < " " or "\x7f" <= ch <= "\x9f"))


def wait_valid(directory, record, waiting):
    """r3-contract.md §3.3: the wait is open, the run is running, its claim's owner is alive, and
    neither the item's nor the run's deadline has passed. Returns (valid, why)."""
    if not isinstance(waiting, dict) or waiting.get("unreadable"):
        return False, "waiting.json is not readable"
    if waiting.get("schema") != WAITING_SCHEMA or waiting.get("waiting") is not True:
        return False, "nothing is waiting for an answer"
    if not isinstance(record, dict) or waiting.get("runId") != record.get("runId"):
        return False, "waiting.json is not this run's"
    if record.get("state") != "running":
        return False, "the run is %s" % record.get("state")
    key = record.get("claimKey")
    try:
        claim = Claim(os.path.dirname(os.path.dirname(directory)), key).read() if isinstance(key, str) else None
    except Usage:
        claim = None
    if not isinstance(claim, dict) or claim.get("runId") != record.get("runId"):
        return False, "the run does not hold the Bridge's claim"
    owner = claim.get("owner") or {}
    if not same_process(owner.get("pid"), owner.get("start")):
        return False, "the run's runner is not running"
    now = utc_now()
    for name, holder in (("item", waiting), ("run", record)):
        deadline = parse_utc(holder.get("deadlineUtc"))
        if deadline is None or now >= deadline:
            return False, "the %s's deadline has passed" % name
    return True, None


def checklist_of(directory):
    """The run's own copy (title, items), or None."""
    try:
        return check_checklist(read_owned(os.path.join(directory, "checklist.json"), MAX_CHECKLIST_BYTES))
    except (OSError, ValueError):
        return None


def waiting_report(directory):
    record = read_json(os.path.join(directory, "run.json"))
    waiting = read_waiting(directory)
    valid, why = wait_valid(directory, record, waiting)
    checklist = checklist_of(directory)
    title, items = checklist if checklist else (None, [])
    texts = {item["id"]: item["text"] for item in items}
    state = (record.get("manualSteps") or {}) if isinstance(record, dict) else {}
    report = {"runId": record.get("runId") if isinstance(record, dict) else None,
              "waiting": isinstance(waiting, dict) and waiting.get("waiting") is True,
              "valid": valid, "why": why,
              "title": visible(title) if title else None,
              "items": [{"id": item.get("id"), "state": item.get("state"), "answer": item.get("answer")}
                        for item in state.get("items") or [] if isinstance(item, dict)]}
    if report["waiting"]:
        item_id = waiting.get("itemId")
        report.update({"waitId": waiting.get("waitId"), "index": waiting.get("index"), "count": waiting.get("count"),
                       "itemId": item_id, "text": visible(texts[item_id]) if item_id in texts else None,
                       "startedAtUtc": waiting.get("startedAtUtc"), "deadlineUtc": waiting.get("deadlineUtc")})
    return report


def publish_answer(directory, wait_id, item_id, answer, memo, by):
    """`answer`: under answer.lock, only to a valid wait with these ids and no answer yet; written
    to a temporary file, flushed, then linked to answer-<waitId>.json (never replacing one)."""
    record = read_json(os.path.join(directory, "run.json"))
    with AnswerLock(directory):
        waiting = read_waiting(directory)
        valid, why = wait_valid(directory, record, waiting)
        if not valid:
            raise Stop("UNKNOWN", "notWaiting", why)
        if waiting.get("waitId") != wait_id or waiting.get("itemId") != item_id:
            raise Stop("UNKNOWN", "otherWait", "the run is waiting for item %s (%s), not this one"
                       % (waiting.get("itemId"), waiting.get("waitId")))
        final = answer_path(directory, wait_id)
        if os.path.lexists(final):
            raise Stop("UNKNOWN", "alreadyAnswered", "item %s has an answer already" % item_id)
        value = {"schema": ANSWER_SCHEMA, "schemaVersion": 1, "runId": record["runId"], "waitId": wait_id,
                 "itemId": item_id, "answer": answer, "memo": memo, "by": by, "writtenAtUtc": iso()}
        data = json.dumps(value, ensure_ascii=False).encode("utf-8")
        if len(data) > MAX_ANSWER_BYTES:
            raise Stop("UNKNOWN", "answerTooLarge", "the answer would be %d bytes" % len(data))
        temporary = os.path.join(directory, ".answer-%s.%s.part" % (wait_id, secrets.token_hex(8)))
        descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        try:
            with os.fdopen(descriptor, "wb") as stream:
                stream.write(data)
                stream.flush()
                os.fsync(stream.fileno())
            # Once more right before the link (the time the answer is taken): a deadline may have
            # passed while it was written.
            valid, why = wait_valid(directory, read_json(os.path.join(directory, "run.json")), read_waiting(directory))
            if not valid or read_waiting(directory).get("waitId") != wait_id:
                raise Stop("UNKNOWN", "notWaiting", why or "the wait changed")
            os.link(temporary, final)
        finally:
            os.unlink(temporary)


def sample_cursor(clients, slot, want, markers):
    """max(marker_cut, observedThroughSeq) of one slot's fixed generation and epoch, or
    (None, why) when the sample is not of them or not read through."""
    entry = entry_of(clients, slot) or {}
    header = markers.get("header") if isinstance(markers.get("header"), dict) else {}
    if (header.get("clientGeneration") != want["clientGeneration"]
            or header.get("observationEpoch") != want["observationEpoch"]
            or entry.get("clientGeneration") != want["clientGeneration"]
            or entry.get("observationEpoch") != want["observationEpoch"]):
        return None, "markersChanged"
    if header.get("catchingUp") is not False or entry.get("catchingUp"):
        return None, "catchingUp"
    if not list_fresh(clients):
        return None, "observationStale"
    through = entry.get("observedThroughSeq")
    if not is_count(through):
        return None, "noCursor"
    return max(marker_cut(markers), through), None


def item_cursor(run, slots, fixed):
    """Per slot, where an item's range starts (None with a reason when it cannot be told)."""
    clients = clients_now(run)
    cursor = {}
    for slot in slots:
        markers = cli_json(run, "log", str(slot), "--markers", "--json", what="markers")
        cursor[slot] = sample_cursor(clients, slot, fixed[slot], markers)
    return cursor


INCOMPLETE_REASONS = ("observationStale", "observationIncomplete", "clientsUnavailable", "clientsInvalid",
                      "bridgeUnavailable", "bridgeInvalid", "childDeadline")
MANUAL_READ_SECONDS = 5  # one look while waiting: short, so an answer and the grace are not held up


def manual_wait(run, item, wait_id, item_id, deadline_monotonic, slots, fixed, guard):
    """Waits for this wait's answer; checks the slots every MANUAL_RECHECK_SECONDS. Returns the
    answer, or raises Stop (answerTimeout, clientChanged, observationIncomplete, invalidAnswer,
    CANCELED, runDeadline). The wait is closed under answer.lock whichever way it ends."""
    bridge_before = run.record["bridgeBefore"]
    next_check = time.monotonic() + MANUAL_RECHECK_SECONDS
    incomplete_since = None
    try:
        while True:
            run.check()  # a cancel and the run's deadline come before an answer
            with AnswerLock(run.directory):
                answer = take_answer_locked(run, wait_id, item_id)
                if answer is not None or time.monotonic() >= deadline_monotonic:
                    close_wait_locked(run, wait_id)
                    if answer is not None:
                        return answer
                    raise Stop("UNKNOWN", "answerTimeout", "no answer to item %s" % item_id)
            if time.monotonic() >= next_check:
                looked = time.monotonic()
                # A look never runs past what is left of the grace (it counts from the first look
                # that failed), and stays short so the answer is seen soon after.
                left = OBSERVATION_GRACE_SECONDS - (looked - incomplete_since) if incomplete_since else OBSERVATION_GRACE_SECONDS
                try:
                    recheck(run, slots, fixed, bridge_before, "while waiting for item %s" % item_id, guard,
                            timeout=max(0.5, min(MANUAL_READ_SECONDS, left)))
                    incomplete_since = None
                except Stop as stop:
                    if stop.result != "UNKNOWN" or stop.reason not in INCOMPLETE_REASONS:
                        if stop.result == "UNKNOWN" and stop.reason != "runDeadline":
                            raise Stop("UNKNOWN", "clientChanged", "%s: %s" % (stop.reason, stop.detail))
                        raise
                    incomplete_since = incomplete_since or looked
                    if time.monotonic() - incomplete_since >= OBSERVATION_GRACE_SECONDS:
                        raise Stop("UNKNOWN", "observationIncomplete",
                                   "while waiting for item %s: %s" % (item_id, stop.detail or stop.reason))
                next_check = time.monotonic() + MANUAL_RECHECK_SECONDS
            time.sleep(CHECK_SECONDS)
    except BaseException:
        close_wait(run, wait_id)
        raise


def close_wait(run, wait_id):
    """Closes the wait when it is still this one's (finish, recover and stops call this)."""
    with AnswerLock(run.directory):
        waiting = read_waiting(run.directory)
        if isinstance(waiting, dict) and waiting.get("waiting") is True and waiting.get("waitId") == wait_id:
            close_wait_locked(run, wait_id)


def item_snapshot(run, item, folder, slots, fixed, start):
    """After an answer: every slot's markers in evidence/manual-steps/<i>-<id>/, the (from, to]
    range of each and any gap. A gap marks evidence UNKNOWN; the run goes on."""
    clients = clients_now(run)
    item["seqRange"], item["evidenceGap"] = {}, []
    for slot in slots:
        want = fixed[slot]
        markers = cli_json(run, "log", str(slot), "--markers", "--json", what="markers")
        header = markers.get("header") if isinstance(markers.get("header"), dict) else {}
        start_seq, start_gap = start[slot]
        end_seq, end_gap = sample_cursor(clients, slot, want, markers)
        gap = start_gap or end_gap
        dropped = header.get("droppedThroughSeq") or 0
        if gap is None and end_seq < start_seq:
            gap = "cursorWentBack"
        elif gap is None and (not isinstance(dropped, int) or dropped > start_seq):
            gap = "markersDropped"
        item["seqRange"][str(slot)] = {"from": start_seq, "to": end_seq if gap in (None, "markersDropped") else None}
        if gap:
            item["evidenceGap"].append({"slot": slot, "reason": gap})
        if (header.get("clientGeneration") == want["clientGeneration"]
                and header.get("observationEpoch") == want["observationEpoch"]):
            save_markers(run, folder, slot, want, markers, "item:" + item["id"])
    item["markersTakenAtUtc"] = iso()
    if item["evidenceGap"]:
        run.record["results"]["evidence"] = "UNKNOWN"
    run.save()


def manual_items_result(items):
    answers = [item.get("answer") if item.get("state") == "answered" else None for item in items]
    if "ng" in answers:
        return "FAIL"
    if all(answer == "ok" for answer in answers):
        return "PASS"
    return "UNKNOWN"


def manual_steps(run, fixed, guard):
    """r3-contract.md §4, after deploy-collect passed in the same continue. Returns the word for
    the whole run (and sets record.reason)."""
    record = run.record
    parameters = record["procedure"]["parameters"]
    slots = record["slots"]
    state = record["manualSteps"]
    try:
        data = read_owned(os.path.join(run.directory, "checklist.json"), MAX_CHECKLIST_BYTES)
        title, items = check_checklist(data)
    except (OSError, ValueError):
        raise Stop("UNKNOWN", "procedureInvalid", "the run's checklist.json is missing or changed")
    if hashlib.sha256(data).hexdigest() != parameters.get("checklistSha256"):
        raise Stop("UNKNOWN", "procedureInvalid", "the run's checklist.json is not the one prepared")
    answer_seconds = min(procedure_seconds(parameters, "answerWaitSeconds", MAX_ANSWER_WAIT), ANSWER_WAIT_CAP)
    after = parameters.get("leaveRejoinAfter")
    base = os.path.join(run.directory, "evidence")
    private_directory(base)
    base = os.path.join(base, "manual-steps")
    private_directory(base)
    operation = run.begin("manualSteps", items=len(items))
    try:
        for index, entry in enumerate(state["items"], 1):
            run.check()
            item_id = entry["id"]
            start = item_cursor(run, slots, fixed)
            deadline = min(utc_now() + timedelta(seconds=answer_seconds), parse_utc(record["deadlineUtc"]))
            entry.update({"state": "waiting", "askedAtUtc": iso()})
            wait_id = entry["waitId"] = open_wait(run, index, len(items), item_id, deadline)
            run.save()
            run.progress("ask", item_id, index=index, count=len(items), waitId=wait_id)
            try:
                answer = manual_wait(run, entry, wait_id, item_id,
                                     time.monotonic() + max(0.0, (deadline - utc_now()).total_seconds()),
                                     slots, fixed, guard)
            except Stop as stop:
                entry["state"] = "unanswered" if stop.reason == "answerTimeout" else "interrupted"
                raise
            except KeyboardInterrupt:
                entry["state"] = "interrupted"
                raise
            entry.update({"state": "answered", "answer": answer["answer"], "memo": answer["memo"],
                          "by": answer["by"], "answeredAtUtc": answer["writtenAtUtc"]})
            run.save()
            run.progress("answered", item_id, index=index, answer=answer["answer"])
            folder = os.path.join(base, "%d-%s" % (index, item_id))
            private_directory(folder)
            entry["evidence"] = os.path.relpath(folder, run.directory)
            item_snapshot(run, entry, folder, slots, fixed, start)
            if item_id == after:
                run.end(operation, "PASS")
                fixed, guard = manual_leave_rejoin(run, fixed, guard)
                operation = run.begin("manualSteps", items=len(items), afterLeaveRejoin=True)
    except Stop as stop:
        if operation.get("inProgress"):
            run.end(operation, stop.result, stop.reason, detail=stop.detail)
        raise
    state["result"] = manual_items_result(state["items"])
    record["results"]["manualSteps"] = state["result"]
    run.end(operation, "PASS")
    with step(run, "endSnapshot"):
        info = bridge_now(run)
        check_windows_disk(info)
        folder = os.path.join(base, "end")
        private_directory(folder)
        for slot in slots:
            collect_slot(run, folder, slot, fixed[slot], "end")
        save_lists(run, folder, clients_now(run), bridge_now(run), "end")
    with step(run, "manualStepsFinalCheck"):
        recheck(run, slots, fixed, record["bridgeBefore"], "at the end of manual-steps", guard)
    return manual_overall(record)


def manual_overall(record):
    """r3-contract.md §5: the tools' results first, the human answers last; UNKNOWN before FAIL."""
    results = record["results"]
    words = []
    if results.get("worldBuildMatch") == "FAIL":
        words.append(("FAIL", "otherBuildTag"))
    if record["procedure"]["parameters"].get("leaveRejoinAfter") is not None and results.get("leaveRejoin") != "PASS":
        words.append((results.get("leaveRejoin") or "UNKNOWN", record.get("reason") or "leaveRejoinNotPassed"))
    if results.get("evidence") != "PASS":
        words.append(("UNKNOWN", "evidenceIncomplete"))
    human = results.get("manualSteps")
    if human == "FAIL":
        words.append(("FAIL", "humanNg"))
    elif human != "PASS":
        words.append(("UNKNOWN", "humanNotAllOk"))
    for word in ("UNKNOWN", "FAIL"):
        for found, reason in words:
            if found == word:
                record["reason"] = reason
                return word
    record["reason"] = None
    return "PASS"


def manual_leave_rejoin(run, fixed, guard):
    """The R1b steps after the declared item, with their evidence under manual-steps/leave-rejoin.
    PASS hands on the rejoined slot's new generation and this run's own revision; anything else
    stops the run (nothing is sent again)."""
    record = run.record
    parameters = record["procedure"]["parameters"]
    target = parameters["targetSlot"]
    remaining = [slot for slot in record["slots"] if slot != target]
    state = record["manualSteps"]["leaveRejoin"] = {
        "state": "started", "afterItem": parameters["leaveRejoinAfter"], "targetSlot": target,
        "remainingSlots": remaining, "remainingMarker": parameters.get("remainingMarker"),
        "clientMayBeLeftClosed": False, "evidence": {}, "evidenceGaps": []}
    try:
        word, reason, final, final_guard = leave_rejoin_steps(run, state, fixed, guard, target, remaining, parameters,
                                                              under=("manual-steps", "leave-rejoin"))
    except (Stop, KeyboardInterrupt) as error:
        record["results"]["leaveRejoin"] = error.result if isinstance(error, Stop) else "CANCELED"
        state["state"] = "stopped"
        if state["clientMayBeLeftClosed"]:
            state["note"] = ("slot %d may be left closed (or a client may have started): check `vrcrt.sh status` and "
                             "use Close/Launch to go on; nothing was sent again" % target)
            if isinstance(error, Stop) and error.result != "CANCELED":
                reread(run, state)
        raise
    record["results"]["leaveRejoin"] = word
    state["state"] = "finished"
    if word != "PASS":
        raise Stop(word, reason or "leaveRejoinNotPassed", "the leave and rejoin after item %s" % parameters["leaveRejoinAfter"])
    record["manualSteps"]["fixedAfterRejoin"] = {str(k): v for k, v in final.items()}
    run.save()
    return final, final_guard


def close_lingering_wait(record, directory):
    """An open wait left by a run that ended: closed, and the item it was for interrupted (from
    waiting.json's own index and id, so an item asked just before a crash is found too). A wait
    that cannot be closed now is recorded, and recover tries again. True when none is left open."""
    state = record.get("manualSteps")
    if not isinstance(state, dict):
        return True
    try:
        with AnswerLock(directory):
            waiting = read_waiting(directory)
            if isinstance(waiting, dict) and waiting.get("waiting") is True:
                index, item_id = waiting.get("index"), waiting.get("itemId")
                for item in state.get("items") or []:
                    if (isinstance(item, dict) and item.get("index") == index and item.get("id") == item_id
                            and item.get("state") in ("notAsked", "waiting")):
                        item["state"] = "interrupted"
                write_json(waiting_path(directory), {"schema": WAITING_SCHEMA, "schemaVersion": 1,
                                                     "runId": record.get("runId"), "waiting": False,
                                                     "closedAtUtc": iso(), "lastWaitId": waiting.get("waitId")})
    except OSError as error:
        state["waitNotClosed"] = str(error)[:300]
        return False
    state.pop("waitNotClosed", None)
    return True


def finish_manual(record, directory, result):
    """Run.finish for manual-steps: an open wait closed, the item it was for interrupted, and the
    human result: CANCELED when the run was, otherwise from the answers as given."""
    state = record.get("manualSteps")
    if not isinstance(state, dict):
        return
    close_lingering_wait(record, directory)
    for item in state.get("items") or []:
        if isinstance(item, dict) and item.get("state") == "waiting":
            item["state"] = "interrupted"
    results = record.get("results")
    if isinstance(results, dict):
        if result == "CANCELED":
            results["manualSteps"] = "CANCELED"
        elif results.get("manualSteps") is None:
            results["manualSteps"] = manual_items_result(state.get("items") or [])
        state["result"] = results["manualSteps"]
    if isinstance(results, dict) and state.get("leaveRejoin") is None \
            and (record.get("procedure") or {}).get("parameters", {}).get("leaveRejoinAfter") is not None \
            and results.get("leaveRejoin") is None:
        results["leaveRejoin"] = "CANCELED" if result == "CANCELED" else "UNKNOWN"


def recover(directory):
    """`recover`: finalizes a run whose runner stopped. Refused while its claim's owner is
    active; done under the claim lock, so it never races a running procedure."""
    run = Run(directory)
    if not isinstance(run.record, dict) or run.record.get("schema") != RUN_SCHEMA:
        raise Usage("%s has no run.json of this runner" % directory)
    if not isinstance(run.record.get("claimKey"), str):
        raise Usage("run %s has no claim key; it was not prepared by this runner" % run.record.get("runId"))
    claim = Claim(os.path.dirname(os.path.dirname(directory)), run.record["claimKey"])
    with claim:
        current = claim.read()
        holds = current is not None and current.get("runId") == run.record.get("runId")
        if holds and claim.active(current):
            raise Stop("UNKNOWN", "runActive", claim.active(current))
        if not finalize_interrupted(directory):
            raise Stop("UNKNOWN", "childStillRunning", "a process of this run did not stop; nothing was changed")
        if holds:
            os.unlink(claim.path)
    return Run(directory).record.get("result")


def prune(root, days):
    root = checked_root(root)
    cutoff = utc_now() - timedelta(days=days)
    removed = 0
    runs = os.path.join(root, "runs")
    for name in sorted(os.listdir(runs)):
        path = os.path.join(runs, name)
        if not RUN_ID.fullmatch(name) or os.path.islink(path) or not os.path.isdir(path):
            continue
        try:
            record = read_json(os.path.join(path, "run.json"))
        except (OSError, ValueError):
            continue
        created = parse_utc(record.get("createdAtUtc")) if isinstance(record, dict) else None
        if record.get("schema") != RUN_SCHEMA or created is None or created > cutoff or record.get("state") != "finished":
            continue
        if record.get("children"):
            continue  # kept: the record of a child never confirmed stopped is what stops the next prepare
        shutil.rmtree(path)
        removed += 1
    results = os.path.join(root, "deploy-results")
    if os.path.isdir(results) and not os.path.islink(results):
        for name in sorted(os.listdir(results)):
            path = os.path.join(results, name)
            if not re.fullmatch(r"(attempt-)?\d{8}T\d{9}Z-[0-9a-f]{8}\.json", name) or os.path.islink(path):
                continue
            if datetime.fromtimestamp(os.lstat(path).st_mtime, timezone.utc) < cutoff:
                os.unlink(path)
                removed += 1
    return removed


def editor_open(project):
    """True when a Unity Editor seems to have this project open (lock file held, or a Unity
    process with this -projectPath). macOS Unity's lock behaviour is requires_validation."""
    lock = os.path.join(project, "Temp", "UnityLockfile")
    if os.path.exists(lock):
        try:
            descriptor = os.open(lock, os.O_RDONLY | os.O_NOFOLLOW)
            try:
                fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
                fcntl.flock(descriptor, fcntl.LOCK_UN)
            except OSError:
                return True
            finally:
                os.close(descriptor)
        except OSError:
            return True
    if FIXTURE_DIR and os.environ.get("VRCRT_RUNNER_TEST_NO_PS"):
        return False  # the script tests, whose sandbox has no ps
    try:
        listing = subprocess.run(["/bin/ps", "-axww", "-o", "command="], capture_output=True, text=True, timeout=10,
                                 env={"LANG": "C", "PATH": "/usr/bin:/bin"}).stdout
    except (OSError, subprocess.TimeoutExpired):
        return True
    real = os.path.realpath(project)
    for line in listing.splitlines():
        if "Unity" in line and ("-projectPath " + real in line or "-projectpath " + real in line.lower()):
            return True
    return False


def headless_build(run, args):
    """Builds and deploys with a headless Unity, given the run's whole remaining time and
    --timeout (not prepare's). The token goes to Unity only. Stopping Unity does not withdraw a
    deploy it already sent, and nothing is sent again."""
    project = os.path.realpath(args.project)
    scene = args.scene
    if not os.path.isdir(os.path.join(project, "Assets")):
        raise Usage("--project must name a Unity project")
    if (not scene.startswith("Assets/") or not scene.endswith(".unity") or ".." in scene.split("/")
            or not os.path.isfile(os.path.join(project, scene))):
        raise Usage("--scene must name a saved .unity file under the project's Assets/")
    unity = os.path.expanduser(args.unity or os.environ.get("UNITY_EDITOR", ""))
    if not os.path.isfile(unity):
        raise Usage("--unity or UNITY_EDITOR must name the Unity executable")
    if editor_open(project):
        raise Stop("UNKNOWN", "editorOpen", "close the project's Unity Editor, or use the window's Run button")
    token = os.environ.get("VRCRT_TOKEN") or keychain_token()
    environment = child_environment({
        "VRC_REMOTE_TEST_BRIDGE_URL": os.environ.get("VRCRT_BRIDGE_URL", ""),
        "VRC_REMOTE_TEST_CERT_SHA256": os.environ.get("VRCRT_CERT_SHA256", ""),
        "VRC_REMOTE_TEST_SCENE_PATH": scene,
        "VRC_REMOTE_TEST_RUN_DIR": run.directory,
        "VRC_REMOTE_TEST_WORLD_BUILD_TAG": run.record["worldBuildTag"],
        "VRC_REMOTE_TEST_TOKEN": token,
    })
    environment.pop("VRCRT_TOKEN", None)
    log = os.path.join(run.directory, "unity-editor.log")
    claim = Claim(os.path.dirname(os.path.dirname(run.directory)), run.record["claimKey"])
    with claim:
        run.record["state"] = "building"
        run.save()
    operation = run.begin("headlessBuild")
    try:
        code, _, _ = supervise(run, [unity, "-batchmode", "-buildTarget", "Win64", "-projectPath", project,
                                     "-executeMethod", "VRCRemoteTest.RemoteBuildCommand.ExecuteRemoteBuildHeadless",
                                     "-logFile", log], args.timeout, writes=True, environment=environment,
                               watch=log, watch_limit=MAX_UNITY_LOG)
    except Stop as stop:
        run.end(operation, stop.result, stop.reason,
                note="a deploy already sent is not withdrawn by stopping Unity; nothing was sent again")
        raise
    run.end(operation, "PASS" if code == 0 else "FAIL", None if code == 0 else "unityExit%d" % code)
    with claim:
        run.record["state"] = "prepared"
        run.save()


def keychain_token():
    url = os.environ.get("VRCRT_BRIDGE_URL", "")
    try:
        result = subprocess.run(["/usr/bin/security", "find-generic-password", "-s", "VRCRemoteTest.LanDirect",
                                 "-a", url, "-w"], capture_output=True, text=True, timeout=30,
                                env={"PATH": "/usr/bin:/bin"})
    except (OSError, subprocess.TimeoutExpired):
        result = None
    if result is None or result.returncode != 0 or not result.stdout.strip():
        raise Stop("UNKNOWN", "tokenMissing", "the paired Bridge token is not in macOS Keychain (or set VRCRT_TOKEN)")
    return result.stdout.strip()


def summary(directory):
    record = Run(directory).record or {}
    return {"runId": record.get("runId"), "runDir": directory, "result": record.get("result"),
            "reason": record.get("reason"), "results": record.get("results")}


def detach_continue(directory, token):
    """For the Unity window: checks what continue would refuse, then forks a child that starts
    its own session, puts its standard streams on /dev/null and runs continue, so closing the
    Editor (or its pipes) does not stop it. Prints {"pid", "runDir"} and returns 0."""
    record = Run(directory).record or {}
    if record.get("schema") != RUN_SCHEMA or not isinstance(record.get("claimKey"), str):
        raise Usage("%s has no run.json of this runner" % directory)
    if record.get("state") != "prepared":
        raise Stop("UNKNOWN", "notPrepared", "run %s is %s" % (record.get("runId"), record.get("state")))
    with Claim(os.path.dirname(os.path.dirname(directory)), record["claimKey"]) as claim:
        current = claim.read()
        if not current or current.get("token") != token or current.get("runId") != record.get("runId"):
            raise Stop("UNKNOWN", "claimLost", "this token does not hold the Bridge's claim for this run")
    sys.stdout.flush()
    pid = os.fork()
    if pid == 0:
        code = EXIT_FOR["UNKNOWN"]
        try:
            os.setsid()
            null = os.open(os.devnull, os.O_RDWR)
            for descriptor in (0, 1, 2):
                os.dup2(null, descriptor)
            code = EXIT_FOR[continue_run(directory, token)]
        except BaseException:
            pass
        finally:
            os._exit(code)
    print(json.dumps({"pid": pid, "runDir": directory}))
    return 0


def interrupt(signum, frame):
    raise KeyboardInterrupt


def main(argv):
    # Both explicitly: a process started in the background inherits SIGINT ignored.
    signal.signal(signal.SIGTERM, interrupt)
    signal.signal(signal.SIGINT, interrupt)
    parser = argparse.ArgumentParser(prog="vrcrt_runner.py", description=__doc__.split("\n")[0])
    sub = parser.add_subparsers(dest="command", required=True)

    def procedure_arguments(p):
        p.add_argument("--procedure", choices=PROCEDURES, default="deploy-collect")
        p.add_argument("--target-slot", type=int, help="leave-rejoin: the declared slot to close and launch again")
        p.add_argument("--remaining-marker", help="leave-rejoin: a regular expression a slot that stays must log "
                                                  "after the close (optional)")
        p.add_argument("--unvalidated", action="store_true",
                       help="run a procedure that has not been validated on real clients yet")
        p.add_argument("--checklist", help="manual-steps: the items to ask (vrcrt.checklist JSON)")
        p.add_argument("--leave-rejoin-after", help="manual-steps: after this item's answer, close --target-slot and "
                                                    "launch it again with preconditions (optional)")
        p.add_argument("--answer-wait", type=int, help="manual-steps: seconds to wait for each answer (60-3600, default 1200)")
        p.add_argument("--run-minutes", type=int, help="manual-steps: the whole run's limit (30-120, default 90)")
        p.add_argument("--end-marker", help="deploy-collect: before collecting, wait until each --end-slots slot "
                                            "logged a marker matching this regular expression")
        p.add_argument("--end-slots", help="deploy-collect: the slots that must log --end-marker (default: --slots)")
        p.add_argument("--end-wait", type=int, help="deploy-collect: seconds to wait for --end-marker (60-1500, default 900)")

    p = sub.add_parser("prepare")
    p.add_argument("--slots", required=True)
    procedure_arguments(p)
    p.add_argument("--expect-clients", nargs="*")
    p.add_argument("--world-build-wait", type=int, help="seconds to wait for the build tag (0-600, default 120; 0 looks once)")
    p.add_argument("--owner-pid", type=int)
    p.add_argument("--run-root")
    p = sub.add_parser("continue")
    p.add_argument("--run", required=True)
    p.add_argument("--claim-token", required=True)
    p.add_argument("--detach", action="store_true",
                   help="check, then go on in a new session with no terminal or pipes and print its PID (the Unity window)")
    p = sub.add_parser("run")
    p.add_argument("--build", choices=["headless"], required=True)
    p.add_argument("--project", required=True)
    p.add_argument("--scene", required=True)
    p.add_argument("--slots", required=True)
    procedure_arguments(p)
    p.add_argument("--expect-clients", nargs="*")
    p.add_argument("--world-build-wait", type=int, help="seconds to wait for the build tag (0-600, default 120; 0 looks once)")
    p.add_argument("--unity")
    p.add_argument("--timeout", type=int, default=900)
    p.add_argument("--run-root")
    p = sub.add_parser("recover")
    p.add_argument("--run", required=True)
    p = sub.add_parser("waiting", help="manual-steps: what the run is waiting for (read only)")
    p.add_argument("--run", required=True)
    p.add_argument("--json", action="store_true")
    p = sub.add_parser("answer", help="manual-steps: answer the item the run is waiting for")
    p.add_argument("--run", required=True)
    p.add_argument("--wait-id", required=True)
    p.add_argument("--item", required=True)
    p.add_argument("answer", choices=ANSWERS)
    p.add_argument("--memo", default="")
    p.add_argument("--by", choices=("unity", "cli"), default="cli")
    p = sub.add_parser("prune")
    p.add_argument("--older-than-days", type=int, required=True)
    p.add_argument("--run-root")
    args = parser.parse_args(argv)

    try:
        if args.command == "prepare":
            owner = args.owner_pid if args.owner_pid else os.getppid()
            if process_start(owner) is None:
                raise Usage("--owner-pid %s is not a running process" % owner)
            run, token = prepare(args, owner)
            print(json.dumps({"runId": run.record["runId"], "runDir": run.directory,
                              "worldBuildTag": run.record["worldBuildTag"], "claimToken": token}))
            return 0
        if args.command == "continue":
            if not TOKEN.fullmatch(args.claim_token):
                raise Usage("--claim-token is the one prepare printed")
            directory = run_directory(args.run)
            if args.detach:
                return detach_continue(directory, args.claim_token)
            result = continue_run(directory, args.claim_token)
            print(json.dumps(summary(directory)))
            return EXIT_FOR[result]
        if args.command == "run":
            if args.timeout < 1:
                raise Usage("--timeout must be positive")
            run, token = prepare(args, os.getpid())
            root = os.path.dirname(os.path.dirname(run.directory))
            try:
                headless_build(run, args)
            except BaseException as error:
                stop = error if isinstance(error, Stop) else Stop(
                    "CANCELED" if isinstance(error, KeyboardInterrupt) else "UNKNOWN",
                    "interrupted" if isinstance(error, KeyboardInterrupt) else "usage", error)
                run.finish(stop.result, stop.reason, stop.detail)
                if not run.record.get("children"):
                    with Claim(root, run.record.get("claimKey")) as claim:
                        claim.release(token)
                if isinstance(error, Usage):
                    raise
                print(json.dumps(summary(run.directory)))
                return EXIT_FOR[stop.result]
            result = continue_run(run.directory, token)
            print(json.dumps(summary(run.directory)))
            return EXIT_FOR[result]
        if args.command == "recover":
            directory = run_directory(args.run)
            result = recover(directory)
            print(json.dumps(summary(directory)))
            return EXIT_FOR.get(result, 3)
        if args.command == "waiting":
            directory = run_directory(args.run)
            try:
                report = waiting_report(directory)
            except (OSError, ValueError) as error:
                raise Usage("%s: %s" % (directory, error))
            if args.json:
                print(json.dumps(report, ensure_ascii=True))
            elif report.get("waiting"):
                print("run %s item %s/%s %s%s" % (report["runId"], report.get("index"), report.get("count"),
                                                  report.get("itemId"), "" if report["valid"] else " (%s)" % report["why"]))
                print(report.get("text") or "")
                print("answer before %s" % report.get("deadlineUtc"))
            else:
                print("run %s is not waiting for an answer" % report.get("runId"))
            return 0 if report.get("valid") else 1
        if args.command == "answer":
            directory = run_directory(args.run)
            if not TOKEN.fullmatch(args.wait_id) or not ITEM_ID.fullmatch(args.item):
                raise Usage("--wait-id and --item are the ones `waiting` prints")
            if len(args.memo) > MAX_MEMO_CHARS:
                raise Usage("--memo takes up to %d characters" % MAX_MEMO_CHARS)
            memo = clean_memo(args.memo)
            if not plain_text(memo, 0, MAX_MEMO_CHARS):
                raise Usage("--memo is not plain text")
            try:
                publish_answer(directory, args.wait_id, args.item, args.answer, memo, args.by)
            except Stop as stop:
                sys.stderr.write("vrcrt_runner: not answered: %s%s\n" % (stop.reason, (" (%s)" % stop.detail) if stop.detail else ""))
                return 1
            except (OSError, ValueError) as error:
                sys.stderr.write("vrcrt_runner: not answered: %s\n" % error)
                return 1
            print(json.dumps({"published": True, "waitId": args.wait_id, "itemId": args.item, "answer": args.answer}))
            return 0
        if args.command == "prune":
            if args.older_than_days < 1:
                raise Usage("--older-than-days must be at least 1")
            print(json.dumps({"removed": prune(args.run_root, args.older_than_days)}))
            return 0
    except Usage as usage:
        sys.stderr.write("vrcrt_runner: %s\n" % usage)
        return EXIT_USAGE
    except Stop as stop:
        sys.stderr.write("vrcrt_runner: %s: %s%s\n" % (stop.result, stop.reason, (" (%s)" % stop.detail) if stop.detail else ""))
        return EXIT_FOR[stop.result]
    except KeyboardInterrupt:
        return EXIT_FOR["CANCELED"]
    return EXIT_USAGE


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
