#!/usr/bin/env python3
"""VRC Remote Test runner: fixed test procedures on top of vrcrt.sh.

R1a has one procedure, deploy-collect: save the state before a deploy, read Unity's deploy
result, wait until every declared client is ready (and, for worlds that log one, until each
client shows this build's tag), collect evidence per client generation, and write the whole run
to run.json. It never closes or launches a client. Whether a test passed is not decided here.

    vrcrt_runner.py prepare --slots 0,1 [--expect-clients ...] [--owner-pid PID] [--run-root DIR]
    vrcrt_runner.py continue --run DIR --claim-token TOKEN
    vrcrt_runner.py run --build headless --project DIR --scene Assets/... --slots 0,1 [--unity PATH]
    vrcrt_runner.py recover --run DIR
    vrcrt_runner.py prune --older-than-days N [--run-root DIR]

prepare prints one JSON line (runId, runDir, worldBuildTag, claimToken). The caller then builds
with RemoteBuildApi in its open Editor, passing runDir and worldBuildTag, and calls continue.
`run` does the three steps itself with a headless Unity, refusing when the project is open.

Results are PASS, FAIL, UNKNOWN or CANCELED, also the exit codes 0, 1, 3 and 4 (2: usage). A
missing or stale observation, a full disk or a lost answer is never counted as success.
Python 3.9 or later, standard library only. Log lines and markers are untrusted: they are only
compared with what this run expects, never used to pick a target, a path or a next step.
"""

import argparse
import fcntl
import hashlib
import json
import os
import re
import secrets
import shutil
import signal
import subprocess
import sys
import tempfile
import time
from datetime import datetime, timedelta, timezone

if sys.version_info < (3, 9):
    sys.stderr.write("vrcrt_runner: Python 3.9 or later is needed (this is %d.%d)\n" % sys.version_info[:2])
    sys.exit(2)

RUNNER_VERSION = "0.11.0"
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
TAG_SECONDS = test_setting("VRCRT_RUNNER_TEST_TAG", "120", 0.5, 120)
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
        with open(os.path.join(self.directory, "progress.jsonl"), "a", encoding="utf-8") as stream:
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
        self.save()
        self.progress("finished", result, reason=reason)


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
    return True


# === Observations ===

def clients_now(run, timeout=CLI_SECONDS):
    return cli_json(run, "status", "--json", what="clients", timeout=timeout)


def bridge_now(run, timeout=CLI_SECONDS):
    return cli_json(run, "bridge", "--json", what="bridge", timeout=timeout)


def bridge_identity(info):
    active = info.get("activeConfig") if isinstance(info.get("activeConfig"), dict) else {}
    identity = {name: info.get(name) for name in ("version", "exeSha256", "processStartedAtUtc", "hostGeneration",
                                                  "activeConfigEtag", "savedConfigEtag")}
    identity["mode"] = active.get("VrchatClientMode")
    return identity


def same_bridge(before, now):
    keys = ("processStartedAtUtc", "hostGeneration", "activeConfigEtag", "savedConfigEtag")
    return all(before.get(k) == now.get(k) for k in keys)


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
        if not isinstance(joined, int) or joined <= (base.get("seq") or 0):
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
                      "instance": entry.get("instance"), "profile": entry.get("profile"), "processId": entry.get("processId")})


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


def recheck(run, slots, fixed, bridge_before, when):
    """All declared slots as they were when they became ready, on the same Bridge host, with
    their logs read through."""
    run.check()
    if not same_bridge(bridge_before, bridge_identity(bridge_now(run))):
        raise Stop("UNKNOWN", "bridgeChanged", when)
    clients = clients_now(run)
    if not list_fresh(clients):
        raise Stop("UNKNOWN", "observationStale", when)
    for slot in slots:
        entry, want = entry_of(clients, slot), fixed[slot]
        if entry is None or entry.get("running") is not True:
            raise Stop("UNKNOWN", "unexpectedLeave", "slot %d %s" % (slot, when))
        if entry.get("catchingUp"):
            raise Stop("UNKNOWN", "observationIncomplete", "slot %d %s" % (slot, when))
        for name in ("clientGeneration", "observationEpoch", "joinedSeq", "instance"):
            if entry.get(name) != want[name]:
                raise Stop("UNKNOWN", "unexpectedChange", "slot %d %s changed %s" % (slot, name, when))
        if entry.get("inErrorWorld") is not False or entry.get("loginState") != "authenticated":
            raise Stop("UNKNOWN", "unexpectedChange", "slot %d %s" % (slot, when))
    return clients


def build_tags(text):
    """The vrcrt.build values on one marker line: exact whitespace-separated fields only."""
    return [field[len("vrcrt.build="):] for field in text.split() if field.startswith("vrcrt.build=")]


def wait_world_build(run, slots, fixed, tag):
    """Each slot shows this build's tag after its current join (fixed generation, epoch and
    joinedSeq), read through. PASS only when every tag seen there is this one; any other tag is
    FAIL; nothing, two tags on a line, or only cut or withheld lines is UNKNOWN."""
    if not tag:
        return "UNKNOWN", "noTagForTheseBytes"
    end = time.monotonic() + TAG_SECONDS
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
            after = want["joinedSeq"]
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


def procedure_hash(name, parameters):
    canonical = json.dumps({"procedure": name, "version": RUNNER_VERSION, "parameters": parameters}, sort_keys=True)
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
    created = utc_now()
    run_id = created.strftime("%Y%m%dT%H%M%SZ") + "-" + secrets.token_hex(4)
    deadline_utc = iso(created + timedelta(seconds=RUN_SECONDS))
    directory = os.path.join(root, "runs", run_id)
    os.mkdir(directory, 0o700)
    parameters = {"slots": slots, "expectClients": expect}
    run = Run(directory)
    run.record = {"schema": RUN_SCHEMA, "schemaVersion": 1, "runId": run_id, "createdAtUtc": iso(created),
                  "deadlineUtc": deadline_utc, "state": "preparing",
                  "procedure": {"name": "deploy-collect", "version": RUNNER_VERSION, "parameters": parameters,
                                "sha256": procedure_hash("deploy-collect", parameters)},
                  "tools": tool_versions(), "slots": slots, "worldBuildTag": run_id, "operations": [], "evidence": [],
                  "children": [],
                  "results": {"deploy": None, "launch": None, "clientReadiness": None, "worldBuildMatch": None,
                              "evidence": None, "testVerdict": "notJudged"},
                  "notes": ["PASS means prepared and collected; the test itself is not judged by the runner.",
                            "A world build match from markers assumes the world does not fake them."]}
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
        result = deploy_collect(run)
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
    if not same_bridge(bridge_before, bridge_identity(bridge_now(run))):
        run.end(operation, "UNKNOWN", "bridgeChanged")
        raise Stop("UNKNOWN", "bridgeChanged", "between prepare and the deploy result")
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

    operation = run.begin("waitWorldBuild")
    match, reason = wait_world_build(run, slots, fixed, tag)
    results["worldBuildMatch"] = match
    run.end(operation, match, reason)

    operation = run.begin("recheckReady")
    try:
        recheck(run, slots, fixed, bridge_before, "after readiness")
    except Stop as stop:
        results["clientReadiness"] = "UNKNOWN"
        run.end(operation, stop.result, stop.reason, detail=stop.detail)
        raise
    run.end(operation, "PASS")

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
        recheck(run, slots, fixed, bridge_before, "at the end")
    except Stop as stop:
        run.end(operation, stop.result, stop.reason, detail=stop.detail)
        raise
    run.end(operation, "PASS")
    # Another build's tag means the evidence is of the wrong build; no tag at all stays UNKNOWN
    # in worldBuildMatch only, so worlds without markers can still be prepared and collected.
    if match == "FAIL":
        record["reason"] = "otherBuildTag"
        return "FAIL"
    return "PASS"


def add_evidence(run, path, **fields):
    size = os.path.getsize(path)
    run.evidence_bytes += size
    if run.evidence_bytes > MAX_EVIDENCE_BYTES:
        raise Stop("UNKNOWN", "evidenceTooLarge")
    run.record["evidence"].append(dict({"path": os.path.relpath(path, run.directory), "size": size,
                                        "sha256": sha256_of(path), "takenAtUtc": iso()}, **fields))
    run.save()


def collect_evidence(run, slots, fixed):
    info = bridge_now(run)
    free, exports = info.get("freeDiskBytes"), info.get("stateExportsBytes")
    if not isinstance(free, int) or not isinstance(exports, int):
        raise Stop("UNKNOWN", "windowsDiskUnknown")
    if free < MIN_WINDOWS_FREE or exports > MAX_WINDOWS_EXPORTS:
        raise Stop("UNKNOWN", "windowsDiskLow", "clear %%LOCALAPPDATA%%\\VRCRemoteTest\\state\\exports on Windows "
                                                "(free %d bytes, exports %d bytes)" % (free, exports))
    evidence = os.path.join(run.directory, "evidence")
    private_directory(evidence)
    for slot in slots:
        check_mac_disk(run)
        want = fixed[slot]
        folder = os.path.join(evidence, "slot-%d" % slot, "g%d-e%d" % (want["clientGeneration"], want["observationEpoch"]))
        private_directory(os.path.dirname(folder))
        private_directory(folder)
        markers = cli_json(run, "log", str(slot), "--markers", "--json", what="markers")
        header = markers.get("header") or {}
        if header.get("clientGeneration") != want["clientGeneration"] or header.get("observationEpoch") != want["observationEpoch"]:
            raise Stop("UNKNOWN", "markersChanged", "slot %d" % slot)
        path = os.path.join(folder, "markers.json")
        write_new(path, json.dumps(markers, indent=2, ensure_ascii=True).encode("utf-8"))
        records = markers.get("records") or []
        add_evidence(run, path, kind="markers", slot=slot, clientGeneration=want["clientGeneration"],
                     observationEpoch=want["observationEpoch"],
                     cutAtSeq=max([r.get("seq", 0) for r in records if isinstance(r, dict)] or [0]))
        code, out, err = cli(run, "export-log", str(slot), folder, "--generation", str(want["clientGeneration"]),
                             "--epoch", str(want["observationEpoch"]), timeout=EXPORT_SECONDS)
        if code != 0:
            raise Stop("UNKNOWN", "exportFailed", "slot %d: %s" % (slot, err.strip()[-300:]))
        lines = [line.strip() for line in out.splitlines() if line.strip()]
        if not lines:
            raise Stop("UNKNOWN", "exportFailed", "slot %d: nothing exported" % slot)
        for line in lines:
            real = os.path.realpath(line)
            if os.path.dirname(real) != os.path.realpath(folder) or os.path.islink(line) or not os.path.isfile(real):
                raise Stop("UNKNOWN", "exportFailed", "slot %d: unexpected output" % slot)
            add_evidence(run, real, kind="fullLog", slot=slot, clientGeneration=want["clientGeneration"],
                         observationEpoch=want["observationEpoch"],
                         note="a snapshot up to when it was taken, not necessarily the client's last line")
    for name, value in (("clients.json", clients_now(run)), ("bridge.json", bridge_now(run))):
        path = os.path.join(evidence, name)
        write_new(path, json.dumps(value, indent=2, ensure_ascii=True).encode("utf-8"))
        add_evidence(run, path, kind=name.split(".")[0])


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
    p = sub.add_parser("prepare")
    p.add_argument("--slots", required=True)
    p.add_argument("--expect-clients", nargs="*")
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
    p.add_argument("--expect-clients", nargs="*")
    p.add_argument("--unity")
    p.add_argument("--timeout", type=int, default=900)
    p.add_argument("--run-root")
    p = sub.add_parser("recover")
    p.add_argument("--run", required=True)
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
