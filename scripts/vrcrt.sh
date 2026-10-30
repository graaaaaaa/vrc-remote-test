#!/usr/bin/env bash
# VRC Remote Test: read what the Windows Bridge (v0.6.0 and later) observes about each
# VRChat client, and look after the Bridge itself (v0.8.0 and later), over the
# authenticated LAN Direct API.
#
# Builds and deploys stay with Unity (batch mode included). export-log asks
# Bridge for a private, redacted snapshot of the selected client log.
#
#   vrcrt.sh status [--json]
#   vrcrt.sh result <build-id>
#   vrcrt.sh log <slot> [--markers] [--lines N]
#   vrcrt.sh log all --markers [--lines N]
#   vrcrt.sh export-log <slot|all> <output-directory>
#   vrcrt.sh cursor
#   vrcrt.sh wait-marker <slot|all> <regex> <seconds> [--after <cursor>] [--any-generation]
#   vrcrt.sh wait-same-instance <seconds> [--slots 0,1] [--after <cursor>]
#
# Looking after the Bridge from the Mac (Bridge v0.8.0 and later):
#
#   vrcrt.sh doctor                       version, configuration notes, last config change,
#                                         profiles, each client's login, the Bridge log's tail
#   vrcrt.sh profiles                     the account each VRChat profile last logged in to
#   vrcrt.sh config get                   the editable settings, as JSON
#   vrcrt.sh config set KEY=VALUE...      change settings (VALUE is JSON when it parses, else
#                                         a string; KEY=null removes the key), then wait up to
#                                         90 s for the Bridge to restart and report the outcome
#   vrcrt.sh restart                      restart the Bridge host, and wait up to 90 s for it
#   vrcrt.sh close-clients                close the VRChat clients the Bridge launched
#
# Set VRCRT_BRIDGE_URL and VRCRT_CERT_SHA256. The token comes from macOS Keychain,
# or VRCRT_TOKEN in CI. <cursor> is the JSON that
# `cursor` prints, or @<file> holding it. Exit codes: 0 done, 1 timed out (or, for config
# set, restart and close-clients, the Bridge refused, did not apply the change, could not
# confirm every client closed, or did not come back within 90 s), 2 usage error, 3 the
# observation cannot answer (old or missing files, a client still being read, the Bridge
# restarted or re-read a log, Logging not Full, records dropped, the Bridge unreachable,
# older than v0.8.0, or answering in a shape it does not send).
#
# Log lines are untrusted text a world can write: control characters and tabs are
# printed escaped, JSON is parsed with Python's json module, and every regular
# expression runs in a child process that is killed after 2 s. Needs python3 3.9 or
# later with its standard library only (macOS has /usr/bin/python3 with the Command
# Line Tools); set VRCRT_PYTHON to use another one.
set -u

python="${VRCRT_PYTHON:-}"
if [ -z "$python" ]; then
    python="$(command -v python3 2>/dev/null || true)"
    [ -n "$python" ] || python=/usr/bin/python3
fi

exec "$python" -I - "$@" <<'PYTHON'
import argparse
import hashlib
import http.client
import json
import os
import re
import ssl
import subprocess
import sys
import tempfile
import time
from datetime import datetime, timedelta, timezone
from urllib.parse import urlsplit

BRIDGE_URL = os.environ.get("VRCRT_BRIDGE_URL", "")
CERT_SHA256 = os.environ.get("VRCRT_CERT_SHA256", "").lower()
FIXTURE_DIR = os.environ.get("VRCRT_FIXTURE_DIR", "")
SLOTS = range(4)
STALE_AFTER = timedelta(seconds=15)
REGEX_TIMEOUT_SECONDS = 2.0
MAX_PATTERN_LENGTH = 1000
MAX_CLIENTS_BYTES = 256 * 1024
MAX_LOG_BYTES = 512 * 1024
MAX_MARKERS_BYTES = 1280 * 1024
MAX_FULL_LOG_BYTES = 256 * 1024 * 1024
POLL_SECONDS = 1.0

MAX_ADMIN_BYTES = 1024 * 1024
# The script tests (VRCRT_FIXTURE_DIR) may shorten the wait and the polling interval; a real
# Bridge always gets 90 s and 2 s.
APPLY_TIMEOUT_SECONDS = float(os.environ.get("VRCRT_TEST_APPLY_TIMEOUT", "90")) if FIXTURE_DIR else 90.0
APPLY_POLL_SECONDS = float(os.environ.get("VRCRT_TEST_POLL_SECONDS", "0")) if FIXTURE_DIR else 2.0
MAX_APPLY_POLLS = 45
POLL_REQUEST_TIMEOUT = 10.0
BRIDGE_LOG_LINES = 30
# What one answer may print: a 1 MiB answer must not become megabytes of terminal output.
MAX_CELL_CHARS = 200
MAX_LINE_CHARS = 1000
MAX_TABLE_ROWS = 100
MAX_LIST_ITEMS = 50

EXIT_TIMEOUT = 1
EXIT_NOT_DONE = 1
EXIT_USAGE = 2
EXIT_OBSERVATION = 3

BIDI = set("؜‎‏‪‫‬‭‮⁦⁧⁨⁩")

# Runs in a child process so a pathological pattern can be killed (plan C6).
REGEX_WORKER = r"""
import json, re, sys
data = json.load(sys.stdin)
pattern = re.compile(data["pattern"])
print(json.dumps([i for i, text in enumerate(data["texts"]) if pattern.search(text)]))
"""


class Failure(Exception):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


class Unreachable(Failure):
    """No answer at all (connection refused or reset, a timeout): expected while the Bridge host restarts."""

    def __init__(self, message):
        super().__init__(EXIT_OBSERVATION, message)


def safe(text):
    """Escapes what a terminal would act on: C0/C1 controls (tab included), DEL and bidirectional controls."""
    out = []
    for ch in text:
        code = ord(ch)
        if ch == "\t":
            out.append("\\t")
        elif code < 0x20 or code == 0x7F or 0x80 <= code <= 0x9F:
            out.append("\\x%02x" % code)
        elif ch in BIDI:
            out.append("\\u%04x" % code)
        else:
            out.append(ch)
    return "".join(out)


def share_path(*parts):
    if FIXTURE_DIR:
        return os.path.join(FIXTURE_DIR, *parts)
    if parts == ("status", "vrchat-clients.json"):
        return "/api/v1/clients"
    if len(parts) == 2 and parts[0] == "results" and re.fullmatch(r"[0-9A-Za-z_-]+\.json", parts[1]):
        return "/api/v1/builds/" + parts[1][:-5]
    if len(parts) == 3 and parts[:2] == ("logs", "clients"):
        name = parts[2]
        match = re.fullmatch(r"slot-([0-3])\.(markers\.)?log", name)
        if match:
            return "/api/v1/%s/clients/%s" % ("markers" if match.group(2) else "logs", match.group(1))
    raise Failure(EXIT_USAGE, "unsupported Bridge resource")


def read_bounded(path, limit):
    """Read one pinned HTTPS resource, or a local fixture in the script tests."""
    try:
        if FIXTURE_DIR:
            with open(path, "rb") as stream:
                data = stream.read(limit + 1)
        else:
            parsed = urlsplit(BRIDGE_URL)
            if parsed.scheme != "https" or not parsed.hostname or not parsed.port or parsed.path not in ("", "/"):
                raise Failure(EXIT_USAGE, "set VRCRT_BRIDGE_URL to the Bridge HTTPS address")
            if not re.fullmatch(r"[0-9a-f]{64}", CERT_SHA256):
                raise Failure(EXIT_USAGE, "set VRCRT_CERT_SHA256 to the paired certificate fingerprint")
            token = os.environ.get("VRCRT_TOKEN")
            if not token:
                result = subprocess.run(
                    ["/usr/bin/security", "find-generic-password", "-s", "VRCRemoteTest.LanDirect", "-a", BRIDGE_URL, "-w"],
                    text=True, capture_output=True, check=False)
                if result.returncode:
                    raise Failure(EXIT_OBSERVATION, "Bridge token is missing from macOS Keychain")
                token = result.stdout.strip()
            context = ssl._create_unverified_context()
            conn = http.client.HTTPSConnection(parsed.hostname, parsed.port, context=context, timeout=10)
            try:
                conn.connect()
                actual = hashlib.sha256(conn.sock.getpeercert(binary_form=True)).hexdigest()
                if actual != CERT_SHA256:
                    raise Failure(EXIT_OBSERVATION, "Bridge certificate fingerprint changed; pair again")
                conn.request("GET", path, headers={"Authorization": "Bearer " + token})
                response = conn.getresponse()
                if response.status == 404:
                    return None
                if response.status != 200:
                    raise Failure(EXIT_OBSERVATION, "Bridge returned HTTP %d for %s" % (response.status, path))
                data = response.read(limit + 1)
            finally:
                conn.close()
    except FileNotFoundError:
        return None
    except OSError as error:
        raise Failure(EXIT_OBSERVATION, "cannot read %s: %s" % (path, error))
    if len(data) > limit:
        raise Failure(EXIT_OBSERVATION, "%s is larger than %d bytes; not reading it" % (path, limit))
    return data.decode("utf-8", errors="replace")


def bridge_connection(timeout=30):
    """Connect and check the certificate before returning an authenticated connection."""
    parsed = urlsplit(BRIDGE_URL)
    if parsed.scheme != "https" or not parsed.hostname or not parsed.port or parsed.path not in ("", "/"):
        raise Failure(EXIT_USAGE, "set VRCRT_BRIDGE_URL to the Bridge HTTPS address")
    if not re.fullmatch(r"[0-9a-f]{64}", CERT_SHA256):
        raise Failure(EXIT_USAGE, "set VRCRT_CERT_SHA256 to the paired certificate fingerprint")
    token = os.environ.get("VRCRT_TOKEN")
    if not token:
        result = subprocess.run(
            ["/usr/bin/security", "find-generic-password", "-s", "VRCRemoteTest.LanDirect", "-a", BRIDGE_URL, "-w"],
            text=True, capture_output=True, check=False)
        if result.returncode:
            raise Failure(EXIT_OBSERVATION, "Bridge token is missing from macOS Keychain")
        token = result.stdout.strip()
    context = ssl._create_unverified_context()
    conn = http.client.HTTPSConnection(parsed.hostname, parsed.port, context=context, timeout=timeout)
    try:
        conn.connect()
        actual = hashlib.sha256(conn.sock.getpeercert(binary_form=True)).hexdigest()
        if actual != CERT_SHA256:
            raise Failure(EXIT_OBSERVATION, "Bridge certificate fingerprint changed; pair again")
        return conn, {"Authorization": "Bearer " + token}
    except BaseException:
        conn.close()
        raise


FIXTURE_COUNTS = {}


def fixture_response(method, path, body, headers, timeout):
    """The script tests' stand-in for one request: VRCRT_FIXTURE_DIR/http/<METHOD>_api_v1_<route>.

    A file may start with "SLOW <seconds>" on its own line (an answer that takes that long; past
    `timeout` it is no answer, like a socket timeout), then "HTTP <status>" on its own line (200
    otherwise), or be "DOWN" for no answer. With <name>.1, <name>.2, ... the n-th request reads
    <name>.n, the last one repeating. No file is a 404. Every request is appended to
    http/requests.log as JSON, with the headers it would be sent with (but the token).
    """
    directory = os.path.join(FIXTURE_DIR, "http")
    name = method + "_" + path.split("?", 1)[0].strip("/").replace("/", "_")
    if os.path.isdir(directory):
        with open(os.path.join(directory, "requests.log"), "a", encoding="utf-8") as log:
            log.write(json.dumps({"method": method, "path": path, "headers": headers,
                                  "body": None if body is None else body.decode("utf-8")}) + "\n")
    count = FIXTURE_COUNTS[name] = FIXTURE_COUNTS.get(name, 0) + 1
    base = os.path.join(directory, name)
    if os.path.exists(base + ".1"):
        n = count
        while n > 1 and not os.path.exists("%s.%d" % (base, n)):
            n -= 1
        base = "%s.%d" % (base, n)
    try:
        with open(base, "rb") as stream:
            data = stream.read()
    except FileNotFoundError:
        return 404, b""
    match = re.match(rb"SLOW (\d+(?:\.\d+)?)\r?\n", data)
    if match:
        delay, data = float(match.group(1)), data[match.end():]
        if delay > timeout:
            time.sleep(max(0.0, timeout))
            raise Unreachable("cannot reach the Bridge: timed out")
        time.sleep(delay)
    if data.startswith(b"DOWN"):
        raise Unreachable("cannot reach the Bridge: connection refused")
    match = re.match(rb"HTTP (\d{3})\r?\n", data)
    if match:
        return int(match.group(1)), data[match.end():]
    return 200, data


def request_headers(body, if_match):
    """The headers of a management request, besides Authorization."""
    headers = {}
    if body is not None:
        headers["Content-Type"] = "application/json"
    if if_match is not None:
        headers["If-Match"] = '"%s"' % if_match
    return headers


def unreachable(error):
    return Unreachable("cannot reach the Bridge: %s" % (getattr(error, "strerror", None) or error or "timed out"))


def bridge_request(method, path, body=None, if_match=None, limit=MAX_ADMIN_BYTES, timeout=30, deadline=None):
    """(status, text) for one request to the management API; Unreachable when nothing answered.

    With `deadline` (a time.monotonic() value) the whole request, body included, ends by then:
    every socket wait is cut to the time left, and the body is read in chunks between checks.
    """
    def time_left():
        if deadline is None:
            return timeout
        left = min(timeout, deadline - time.monotonic())
        if left <= 0:
            raise Unreachable("cannot reach the Bridge: timed out")
        return left

    headers = request_headers(body, if_match)
    if FIXTURE_DIR:
        status, data = fixture_response(method, path, body, headers, time_left())
    else:
        try:
            conn, auth = bridge_connection(time_left())
        except (OSError, http.client.HTTPException) as error:
            raise unreachable(error)
        # Kept: getresponse() hands the socket to the response and may clear conn.sock.
        sock = conn.sock
        try:
            conn.request(method, path, body=body, headers={**auth, **headers})
            response = conn.getresponse()
            status = response.status
            chunks, size = [], 0
            read = getattr(response, "read1", response.read)
            while size <= limit:
                sock.settimeout(time_left())
                chunk = read(min(65536, limit + 1 - size))
                if not chunk:
                    break
                chunks.append(chunk)
                size += len(chunk)
            data = b"".join(chunks)
        except (OSError, http.client.HTTPException) as error:
            raise unreachable(error)
        finally:
            conn.close()
    if len(data) > limit:
        raise Failure(EXIT_OBSERVATION, "the Bridge's answer to %s is larger than %d bytes; not reading it" % (path, limit))
    return status, data.decode("utf-8", errors="replace")


def json_of(text):
    """The parsed answer, or None when it is not JSON (too deep, or numbers past Python's limits, included)."""
    try:
        return json.loads(text) if text else None
    except (ValueError, RecursionError):
        return None


def listed(value, what):
    """A JSON array as a list; None (missing) as []; anything else is an invalid Bridge answer."""
    if value is None:
        return []
    if not isinstance(value, list):
        raise Failure(EXIT_OBSERVATION, "the Bridge's %s is not a list" % what)
    return value


def clip(value, limit=MAX_LINE_CHARS):
    """Untrusted text for the terminal: escaped by safe(), then cut to `limit` characters."""
    text = safe(str(value))
    return text if len(text) <= limit else text[:limit - 3] + "..."


def print_items(prefix, values, what, stream=None):
    """One line per item of a JSON array, at most MAX_LIST_ITEMS of them."""
    values = listed(values, what)
    for value in values[:MAX_LIST_ITEMS]:
        print(prefix + clip(value), file=stream or sys.stdout)
    if len(values) > MAX_LIST_ITEMS:
        print("%s... and %d more" % (prefix, len(values) - MAX_LIST_ITEMS), file=stream or sys.stdout)


def error_summary(text):
    """": busy (deploy)" and the like, from an error body; untrusted, so printed through safe()."""
    body = json_of(text)
    if not isinstance(body, dict) or not isinstance(body.get("error"), str):
        return ""
    details = [clip(body[key], MAX_CELL_CHARS) for key in ("operation", "etag", "retryAfterSeconds")
               if body.get(key) is not None]
    return ": %s%s" % (clip(body["error"], MAX_CELL_CHARS), " (%s)" % ", ".join(details) if details else "")


def get_json(path):
    status, text = bridge_request("GET", path)
    if status == 404:
        raise Failure(EXIT_OBSERVATION, "the Bridge has no %s; it is older than v0.8.0 (update it on Windows)" % path)
    if status == 401:
        raise Failure(EXIT_OBSERVATION, "the Bridge rejected the pairing token")
    if status != 200:
        raise Failure(EXIT_OBSERVATION, "Bridge returned HTTP %d for %s%s" % (status, path, error_summary(text)))
    value = json_of(text)
    if not isinstance(value, dict):
        raise Failure(EXIT_OBSERVATION, "Bridge returned invalid JSON for %s" % path)
    return value


def refused(what, status, text):
    """Prints why the Bridge did not do it (error, problems, the rest of the body) and returns EXIT_NOT_DONE."""
    print("%s: the Bridge answered HTTP %d%s" % (what, status, error_summary(text)), file=sys.stderr)
    body = json_of(text)
    if isinstance(body, dict):
        print_items("  - ", body.get("problems"), "list of problems", sys.stderr)
    elif text:
        print("  %s" % clip(text, 500), file=sys.stderr)
    return EXIT_NOT_DONE


def host_generation(info):
    generation = info.get("hostGeneration")
    if type(generation) is not int:
        raise Failure(EXIT_OBSERVATION, "Bridge returned no hostGeneration")
    return generation


def wait_for_host(before, operation_id=None):
    """GET /bridge every APPLY_POLL_SECONDS until the host generation passes `before` (and, for a
    configuration change, lastConfigApply reports `operation_id` as no longer pending). The info,
    or None after APPLY_TIMEOUT_SECONDS. No answer while the host restarts is expected."""
    deadline = time.monotonic() + APPLY_TIMEOUT_SECONDS
    for _ in range(MAX_APPLY_POLLS):
        time.sleep(max(0.0, min(APPLY_POLL_SECONDS, deadline - time.monotonic())))
        if time.monotonic() >= deadline:
            break
        try:
            status, text = bridge_request("GET", "/api/v1/bridge", timeout=POLL_REQUEST_TIMEOUT, deadline=deadline)
        except Unreachable:
            continue
        if time.monotonic() > deadline:
            break  # An answer that came after the deadline does not count.
        info = json_of(text) if status == 200 else None
        if not isinstance(info, dict) or type(info.get("hostGeneration")) is not int or info["hostGeneration"] <= before:
            continue
        if operation_id is None:
            return info
        last = info.get("lastConfigApply")
        if isinstance(last, dict) and last.get("operationId") == operation_id and last.get("outcome") != "pending":
            return info
    return None


def human_bytes(value):
    if type(value) is not int or value < 0 or value >= 1 << 63:
        return "unknown"
    size = float(value)
    for unit in ("bytes", "KiB", "MiB", "GiB"):
        if size < 1024 or unit == "GiB":
            return "%d bytes" % value if unit == "bytes" else "%.1f %s" % (size, unit)
        size /= 1024
    return "unknown"


def cmd_doctor(args):
    try:
        info = get_json("/api/v1/bridge")
    except Unreachable:
        raise
    except Failure as failure:
        print("Bridge: %s" % safe(str(failure)))
        info = None
    if info is not None:
        cell = lambda key, source=info: clip(show(source.get(key)), MAX_CELL_CHARS)
        print("Bridge        %s" % cell("version"))
        print("Executable    SHA-256 %s" % cell("exeSha256"))
        print("Host          generation %s, started %s (process started %s)" % (
            cell("hostGeneration"), cell("hostStartedAtUtc"), cell("processStartedAtUtc")))
        print("Listening     %s" % cell("listenUrl"))
        busy = info.get("busy")
        print("Busy          %s%s" % ("no" if busy is None else clip(busy, MAX_CELL_CHARS),
                                     ", restart pending" if info.get("restartPending") is True else ""))
        active, saved = info.get("activeConfigEtag"), info.get("savedConfigEtag")
        print("Config        %s" % ("as saved" if active == saved else "config.json changed since this host started"))
        config = info.get("activeConfig")
        if isinstance(config, dict):
            keys = sorted(config, key=str)
            for key in keys[:MAX_LIST_ITEMS]:
                value = json.dumps(config[key], ensure_ascii=True) if not isinstance(config[key], str) else config[key]
                print("  %s = %s" % (clip(key, MAX_CELL_CHARS), clip(value, MAX_CELL_CHARS)))
            if len(keys) > MAX_LIST_ITEMS:
                print("  ... and %d more" % (len(keys) - MAX_LIST_ITEMS))
        paths = info.get("paths") if isinstance(info.get("paths"), dict) else {}
        for key, label in (("vrchatWorldsDirectory", "Worlds dir"), ("vrchatExecutable", "VRChat.exe")):
            entry = paths.get(key) if isinstance(paths.get(key), dict) else {}
            print("%-13s %s%s" % (label, cell("display", entry), "" if entry.get("exists") is not False else " (missing)"))
        print("Disk          %s free, %s in log exports" % (human_bytes(info.get("freeDiskBytes")),
                                                          human_bytes(info.get("stateExportsBytes"))))
        last = info.get("lastConfigApply")
        if isinstance(last, dict):
            print("Last change   %s at %s (operation %s)" % (cell("outcome", last), cell("atUtc", last),
                                                            cell("operationId", last)))
            print_items("  - ", last.get("problems"), "list of problems")
        else:
            print("Last change   none through the LAN API since the Bridge started")
        print_items("note: ", info.get("configNotes"), "configNotes")

    print()
    try:
        print_profiles(get_json("/api/v1/profiles"))
    except Unreachable:
        raise
    except Failure as failure:
        print("Profiles: %s" % safe(str(failure)))

    print()
    try:
        clients = load_clients(required=False)
    except Failure as failure:
        clients = False
        print("Clients: %s" % safe(str(failure)))
    if clients is None:
        print("Clients: the Bridge has published no client list yet.")
    elif clients:
        if is_stale(clients):
            print("warning: the client list is older than 15 s; the Bridge may not be running")
        rows = [("SLOT", "RUNNING", "PROFILE", "LOGIN", "ACCOUNT", "DIGEST", "AUTHENTICATED")]
        for c in sorted(clients["clients"], key=lambda c: c["slot"]):
            account = c.get("account") if isinstance(c.get("account"), dict) else {}
            running = c.get("running")
            profile = c.get("profile")
            rows.append((c["slot"],
                         show(running) if running is None or isinstance(running, bool) else "invalid",
                         show(profile) if profile is None or (type(profile) is int and 0 <= profile <= 99) else "invalid",
                         show(c.get("loginState")), show(account.get("displayName")), show(account.get("idDigest")),
                         show(account.get("authenticatedAtUtc"))))
        if len(rows) == 1:
            print("No VRChat client has had a slot yet.")
        else:
            print_table(rows)

    print()
    try:
        status, text = bridge_request("GET", "/api/v1/logs/bridge?lines=%d" % BRIDGE_LOG_LINES)
    except Unreachable:
        raise
    if status == 200:
        print("Bridge log (last %d lines):" % BRIDGE_LOG_LINES)
        lines = text.split("\n")
        if lines and lines[-1] == "":
            lines.pop()
        for line in lines[-BRIDGE_LOG_LINES:]:
            print("  " + clip(line))
    else:
        print("Bridge log: HTTP %d%s" % (status, error_summary(text)))
    return 0 if info is not None else EXIT_OBSERVATION


def print_profiles(data):
    configured = data.get("configuredProfiles")
    if configured is None:
        print("Configured profiles: none (Reload mode)")
    else:
        configured = listed(configured, "configuredProfiles")
        print("Configured profiles: %s" % clip(",".join(str(p) for p in configured[:MAX_LIST_ITEMS]), MAX_CELL_CHARS))
    rows = [("PROFILE", "ACCOUNT", "DIGEST", "AUTHENTICATED", "SLOT", "GENERATION")]
    for p in listed(data.get("profiles"), "list of profiles"):
        if not isinstance(p, dict):
            raise Failure(EXIT_OBSERVATION, "the Bridge's list of profiles holds something other than profiles")
        rows.append((show(p.get("profile")), show(p.get("displayName")), show(p.get("idDigest")),
                     show(p.get("authenticatedAtUtc")), show(p.get("slot")), show(p.get("clientGeneration"))))
    if len(rows) == 1:
        print("No profile has been seen logged in yet.")
    else:
        print_table(rows)
    print_items("warning: ", data.get("warnings"), "list of warnings")


def cmd_profiles(args):
    print_profiles(get_json("/api/v1/profiles"))
    return 0


def cmd_config_get(args):
    print(json.dumps(get_json("/api/v1/config"), indent=2, ensure_ascii=True))
    return 0


def reject_constant(name):
    raise ValueError(name)


def parse_assignments(items):
    """KEY=VALUE pairs as a JSON Merge Patch: VALUE is JSON when it parses (true, 3, [0,1], null), else a string."""
    patch = {}
    for item in items:
        key, eq, raw = item.partition("=")
        if not eq or not re.fullmatch(r"[A-Za-z][A-Za-z0-9]{0,63}", key):
            raise Failure(EXIT_USAGE, "expected KEY=VALUE, not %r" % item)
        if key in patch:
            raise Failure(EXIT_USAGE, "%s is given more than once" % key)
        try:
            patch[key] = json.loads(raw, parse_constant=reject_constant)
        except (ValueError, RecursionError):
            patch[key] = raw
    return patch


def cmd_config_set(args):
    patch = parse_assignments(args.assignments)
    before = host_generation(get_json("/api/v1/bridge"))
    etag = get_json("/api/v1/config").get("etag")
    if not isinstance(etag, str) or not re.fullmatch(r"[A-Za-z0-9._-]{1,128}", etag):
        raise Failure(EXIT_OBSERVATION, "Bridge returned no usable etag from /api/v1/config")
    status, text = bridge_request("PUT", "/api/v1/config", body=json.dumps(patch).encode("utf-8"), if_match=etag)
    if status != 202:
        return refused("config set", status, text)
    accepted = json_of(text)
    operation_id = accepted.get("operationId") if isinstance(accepted, dict) else None
    if not isinstance(operation_id, str) or not re.fullmatch(r"[0-9A-Za-z-]{1,64}", operation_id):
        raise Failure(EXIT_OBSERVATION, "Bridge accepted the change but returned no operationId")
    print("config.json written (operation %s); waiting for the Bridge to restart..." % operation_id, file=sys.stderr)
    info = wait_for_host(before, operation_id)
    if info is None:
        print("config set: the Bridge did not report operation %s within %d s; check `vrcrt.sh doctor`"
              % (operation_id, APPLY_TIMEOUT_SECONDS), file=sys.stderr)
        return EXIT_NOT_DONE
    last = info["lastConfigApply"]
    print("outcome: %s" % clip(last.get("outcome"), MAX_CELL_CHARS))
    print_items("  - ", last.get("problems"), "list of problems")
    print_items("note: ", info.get("configNotes"), "configNotes")
    return 0 if last.get("outcome") == "applied" else EXIT_NOT_DONE


def cmd_restart(args):
    before = host_generation(get_json("/api/v1/bridge"))
    status, text = bridge_request("POST", "/api/v1/bridge/restart")
    if status != 202:
        return refused("restart", status, text)
    print("restart scheduled; waiting for the Bridge host...", file=sys.stderr)
    info = wait_for_host(before)
    if info is None:
        print("restart: the Bridge host did not come back within %d s; check `vrcrt.sh doctor`"
              % APPLY_TIMEOUT_SECONDS, file=sys.stderr)
        return EXIT_NOT_DONE
    print("restarted: host generation %d" % info["hostGeneration"])
    return 0


def cmd_close_clients(args):
    status, text = bridge_request("POST", "/api/v1/clients/close")
    body = json_of(text)
    if status in (200, 409) and isinstance(body, dict) and "closed" in body:
        counts = [body.get(key) for key in ("closed", "notConfirmed", "othersRunning")]
        if any(type(count) is not int or not 0 <= count <= 1000 for count in counts):
            raise Failure(EXIT_OBSERVATION, "the Bridge's answer to close-clients has invalid counts")
        closed, not_confirmed, others = counts
        print("closed %d, not confirmed %d, others running %d" % (closed, not_confirmed, others))
        if body.get("problem"):
            print("problem: %s" % clip(body["problem"]))
        if not_confirmed:
            print("close-clients: %d VRChat client(s) may still be running; check `vrcrt.sh status`" % not_confirmed,
                  file=sys.stderr)
        return 0 if status == 200 and not not_confirmed else EXIT_NOT_DONE
    return refused("close-clients", status, text)


def export_one_log(slot, client, directory):
    body = json.dumps({"slot": slot, "clientGeneration": client["clientGeneration"],
                       "observationEpoch": client["observationEpoch"]}).encode("utf-8")
    conn, headers = bridge_connection()
    try:
        conn.request("POST", "/api/v1/log-exports", body=body,
                     headers={**headers, "Content-Type": "application/json"})
        response = conn.getresponse()
        metadata_bytes = response.read(8193)
        if response.status != 200:
            raise Failure(EXIT_OBSERVATION, "slot %d log export failed (HTTP %d); client session may have changed"
                          % (slot, response.status))
        if len(metadata_bytes) > 8192:
            raise Failure(EXIT_OBSERVATION, "Bridge log export metadata is too large")
        try:
            metadata = json.loads(metadata_bytes)
        except (ValueError, RecursionError):
            raise Failure(EXIT_OBSERVATION, "Bridge returned invalid log export metadata")
    finally:
        conn.close()
    if not isinstance(metadata, dict):
        raise Failure(EXIT_OBSERVATION, "Bridge returned invalid log export metadata")
    export_id = metadata.get("exportId")
    size = metadata.get("size")
    sha256 = metadata.get("sha256")
    if (not isinstance(export_id, str) or not re.fullmatch(r"[0-9a-f]{32}", export_id)
            or type(size) is not int or not 0 <= size <= MAX_FULL_LOG_BYTES
            or not isinstance(sha256, str) or not re.fullmatch(r"[0-9a-f]{64}", sha256)):
        raise Failure(EXIT_OBSERVATION, "Bridge returned invalid log export metadata")
    filename = "vrchat-slot-%d-generation-%d-epoch-%d-%s.log" % (
        slot, client["clientGeneration"], client["observationEpoch"], export_id)
    destination = os.path.join(directory, filename)
    conn, headers = bridge_connection()
    partial = None
    try:
        conn.request("GET", "/api/v1/log-exports/" + export_id, headers=headers)
        response = conn.getresponse()
        if response.status != 200:
            raise Failure(EXIT_OBSERVATION, "slot %d log download failed (HTTP %d)" % (slot, response.status))
        with tempfile.NamedTemporaryFile(prefix=".vrcrt-log-", suffix=".part", dir=directory, delete=False) as output:
            partial = output.name
            digest = hashlib.sha256()
            count = 0
            while True:
                chunk = response.read(65536)
                if not chunk:
                    break
                count += len(chunk)
                if count > size or count > MAX_FULL_LOG_BYTES:
                    raise Failure(EXIT_OBSERVATION, "slot %d log is larger than announced" % slot)
                output.write(chunk)
                digest.update(chunk)
            output.flush()
            os.fsync(output.fileno())
        if count != size or digest.hexdigest() != sha256:
            raise Failure(EXIT_OBSERVATION, "slot %d log failed size or SHA-256 verification" % slot)
        os.link(partial, destination)  # Fails if a file with this name already exists.
        print(destination)
    finally:
        conn.close()
        if partial and os.path.exists(partial):
            os.unlink(partial)


def cmd_export_log(args):
    slot = parse_slot(args.slot, allow_all=True)
    directory = os.path.abspath(args.output_directory)
    if not os.path.isdir(directory):
        raise Failure(EXIT_USAGE, "output directory does not exist: %s" % directory)
    clients = load_clients()
    if is_stale(clients):
        raise Failure(EXIT_OBSERVATION, "client list is older than 15 s; refusing log export")
    selected = ([entry_of(clients, slot)] if slot is not None
                else [c for c in clients["clients"] if c.get("running") is True])
    if not selected or any(c is None for c in selected):
        raise Failure(EXIT_OBSERVATION, "no matching VRChat client is running")
    for client in selected:
        if (client.get("logStatus") != "shown" or type(client.get("clientGeneration")) is not int
                or type(client.get("observationEpoch")) is not int):
            raise Failure(EXIT_OBSERVATION, "slot %d has no verified current log source" % client["slot"])
    for client in selected:
        try:
            export_one_log(client["slot"], client, directory)
        except (OSError, http.client.HTTPException) as error:
            raise Failure(EXIT_OBSERVATION, "slot %d log export failed: %s" %
                          (client["slot"], getattr(error, "strerror", None) or str(error)))
    return 0


def parse_utc(value):
    """.NET's round-trip form, with 1 to 7 fractional digits ("2026-10-18T04:34:37.1234567+00:00")."""
    match = re.fullmatch(r"(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,7}))?(Z|[+-]\d{2}:\d{2})", value or "")
    if not match:
        return None
    base, fraction, offset = match.groups()
    micro = int((fraction or "0").ljust(6, "0")[:6])
    stamp = datetime.strptime(base, "%Y-%m-%dT%H:%M:%S").replace(microsecond=micro)
    if offset == "Z":
        return stamp.replace(tzinfo=timezone.utc)
    sign = 1 if offset[0] == "+" else -1
    hours, minutes = int(offset[1:3]), int(offset[4:6])
    return stamp.replace(tzinfo=timezone(sign * timedelta(hours=hours, minutes=minutes)))


def load_clients(required=True):
    text = read_bounded(share_path("status", "vrchat-clients.json"), MAX_CLIENTS_BYTES)
    if text is None:
        if required:
            raise Failure(
                EXIT_OBSERVATION,
                "Bridge client status is unavailable at %s" % BRIDGE_URL)
        return None
    try:
        data = json.loads(text)
    except (ValueError, RecursionError) as error:
        raise Failure(EXIT_OBSERVATION, "status/vrchat-clients.json is not valid JSON (%s)" % type(error).__name__)
    if not isinstance(data, dict) or not isinstance(data.get("clients"), list):
        raise Failure(EXIT_OBSERVATION, "status/vrchat-clients.json has no clients list")
    data["clients"] = [c for c in data["clients"]
                       if isinstance(c, dict) and type(c.get("slot")) is int and c["slot"] in SLOTS]
    return data


def age(clients):
    updated = parse_utc(clients.get("updatedAtUtc"))
    return None if updated is None else datetime.now(timezone.utc) - updated


def is_stale(clients):
    elapsed = age(clients)
    return elapsed is None or elapsed > STALE_AFTER


def entry_of(clients, slot):
    for client in clients["clients"]:
        if client.get("slot") == slot:
            return client
    return None


def logging_not_full(entry):
    return entry is not None and entry.get("loggingLevel") in ("off", "errorsOnly")


def read_markers(slot):
    """(header dict or None, [(seq, text)]) for the slot's markers file; (None, []) when it does not exist."""
    text = read_bounded(share_path("logs", "clients", "slot-%d.markers.log" % slot), MAX_MARKERS_BYTES)
    if text is None:
        return None, []
    lines = text.split("\n")
    header = None
    start = 0
    if lines and lines[0].startswith("# "):
        start = 1
        try:
            header = json.loads(lines[0][2:])
        except (ValueError, RecursionError):
            header = None
        if not isinstance(header, dict):
            header = None
    records = []
    for line in lines[start:]:
        seq, tab, rest = line.partition("\t")
        if tab and seq.isdigit():
            records.append((int(seq), rest))
    return header, records


def record_time(text):
    try:
        return datetime.strptime(text[:19], "%Y.%m.%d %H:%M:%S")
    except ValueError:
        return None


def merge(per_slot):
    """All slots' records by the time in their header, then slot, then seq; a record without a time keeps the one before it."""
    keyed = []
    for slot, records in per_slot:
        last = datetime.min
        for seq, text in sorted(records):
            last = record_time(text) or last
            keyed.append((last, slot, seq, text))
    keyed.sort(key=lambda k: (k[0], k[1], k[2]))
    return keyed


def search(pattern, texts):
    """Indexes of the texts the pattern matches, in a child process killed after REGEX_TIMEOUT_SECONDS."""
    if not texts:
        return []
    try:
        result = subprocess.run(
            [sys.executable, "-I", "-c", REGEX_WORKER],
            input=json.dumps({"pattern": pattern, "texts": texts}),
            capture_output=True,
            text=True,
            timeout=REGEX_TIMEOUT_SECONDS,
        )
    except subprocess.TimeoutExpired:
        raise Failure(EXIT_USAGE, "the regular expression took more than %.0f s; simplify it" % REGEX_TIMEOUT_SECONDS)
    if result.returncode != 0:
        lines = result.stderr.strip().splitlines()
        raise Failure(EXIT_USAGE, "invalid regular expression: %s" % (lines[-1] if lines else "unknown error"))
    return json.loads(result.stdout)


def check_pattern(pattern):
    if len(pattern) > MAX_PATTERN_LENGTH:
        raise Failure(EXIT_USAGE, "the regular expression is longer than %d characters" % MAX_PATTERN_LENGTH)
    search(pattern, [""])


def take_cursor(clients):
    if is_stale(clients):
        raise Failure(EXIT_OBSERVATION, "status/vrchat-clients.json is older than 15 s; is the Bridge running?")
    slots = {}
    for client in clients["clients"]:
        if client.get("running") is True and client.get("catchingUp"):
            raise Failure(
                EXIT_OBSERVATION,
                "slot %d is still being read from its start (catchingUp); try again shortly" % client["slot"])
        slots[str(client["slot"])] = {
            "clientGeneration": client.get("clientGeneration"),
            "observationEpoch": client.get("observationEpoch"),
            "seq": client.get("observedThroughSeq") or 0,
            "running": client.get("running"),
        }
    return {
        "schemaVersion": 1,
        "bridgeSessionId": clients.get("bridgeSessionId"),
        "takenAtUtc": clients.get("updatedAtUtc"),
        "slots": slots,
    }


def parse_cursor(value):
    if value.startswith("@"):
        try:
            with open(value[1:], encoding="utf-8") as stream:
                value = stream.read()
        except OSError as error:
            raise Failure(EXIT_USAGE, "cannot read the cursor file: %s" % (error.strerror or error))
    try:
        cursor = json.loads(value)
    except (ValueError, RecursionError):
        raise Failure(EXIT_USAGE, "--after needs the JSON that `cursor` prints, or @<file> holding it")
    if not isinstance(cursor, dict) or not isinstance(cursor.get("slots"), dict):
        raise Failure(EXIT_USAGE, "--after is not a cursor")
    return {int(k): v for k, v in cursor["slots"].items() if k.isdigit() and isinstance(v, dict)}


def parse_slot(value, allow_all):
    if allow_all and value == "all":
        return None
    if value.isdigit() and int(value) in SLOTS:
        return int(value)
    raise Failure(EXIT_USAGE, "a slot is 0 to 3%s, not %r" % (" or all" if allow_all else "", value))


def print_table(rows):
    """Rows of untrusted values: every cell is escaped and cut to MAX_CELL_CHARS, and at most
    MAX_TABLE_ROWS rows after the header are printed, all before the widths are worked out."""
    hidden = max(0, len(rows) - 1 - MAX_TABLE_ROWS)
    rows = [[clip(cell, MAX_CELL_CHARS) for cell in row] for row in rows[:1 + MAX_TABLE_ROWS]]
    widths = [max(len(row[i]) for row in rows) for i in range(len(rows[0]))]
    for row in rows:
        print("  ".join(cell.ljust(width) for cell, width in zip(row, widths)).rstrip())
    if hidden:
        print("... and %d more" % hidden)


def show(value):
    return "-" if value is None else ("yes" if value is True else ("no" if value is False else value))


def cmd_status(args):
    clients = load_clients()
    if args.json:
        print(json.dumps(clients, indent=2, ensure_ascii=True))
        return 0
    elapsed = age(clients)
    if is_stale(clients):
        print("warning: status/vrchat-clients.json is %s old; the Bridge may not be running"
              % ("of unknown age" if elapsed is None else "%d s" % elapsed.total_seconds()), file=sys.stderr)
    if clients.get("processListReadable") is False:
        print("warning: the Bridge could not read the VRChat process list this time", file=sys.stderr)
    rows = [("SLOT", "PID", "PROFILE", "RUNNING", "STATE", "INSTANCE", "PLAYERS", "MASTER", "LOGGING", "AUTH", "EPOCH", "SEQ", "CATCHUP")]
    for c in sorted(clients["clients"], key=lambda c: c["slot"]):
        players = c.get("playersInInstance")
        rows.append((
            c["slot"], show(c.get("processId")), show(c.get("profile")), show(c.get("running")), show(c.get("state")),
            safe(str(show(c.get("instance")))), "-" if players is None else len(players), show(c.get("isMaster")),
            show(c.get("loggingLevel")), show(c.get("authenticated")), show(c.get("observationEpoch")),
            show(c.get("observedThroughSeq")), show(c.get("catchingUp"))))
    if len(rows) == 1:
        print("No VRChat client has had a slot yet.")
    else:
        print_table(rows)
    for c in sorted(clients["clients"], key=lambda c: c["slot"]):
        for warning in c.get("warnings") or []:
            print("slot %d: %s" % (c["slot"], safe(str(warning))))
    if clients.get("unassignedClients"):
        print("%d VRChat process(es) have no slot (not checked, unknown start time, or a fifth client)." % clients["unassignedClients"])
    return 0


def cmd_result(args):
    if not re.fullmatch(r"[0-9A-Za-z_-]{1,128}", args.build_id):
        raise Failure(EXIT_USAGE, "invalid build ID")
    value = read_bounded(share_path("results", args.build_id + ".json"), 64 * 1024)
    if value is None:
        raise Failure(EXIT_OBSERVATION, "build result is not available")
    try:
        result = json.loads(value)
    except (ValueError, RecursionError):
        raise Failure(EXIT_OBSERVATION, "build result is not valid JSON")
    if not isinstance(result, dict) or result.get("buildId") != args.build_id:
        raise Failure(EXIT_OBSERVATION, "build result ID does not match")
    print(json.dumps(result, indent=2, ensure_ascii=True))
    return 0 if result.get("status") == "deployed" else EXIT_OBSERVATION


def cmd_log(args):
    slot = parse_slot(args.slot, allow_all=True)
    if slot is None and not args.markers:
        raise Failure(EXIT_USAGE, "`log all` needs --markers (each slot's log is read on its own)")
    if args.markers:
        slots = list(SLOTS) if slot is None else [slot]
        per_slot = []
        for s in slots:
            header, records = read_markers(s)
            if header is not None and (header.get("catchingUp") or header.get("droppedRecords")):
                print("note: slot %d %s" % (s, "is still being read from its start"
                      if header.get("catchingUp") else "dropped %s older records" % header.get("droppedRecords")), file=sys.stderr)
            per_slot.append((s, records))
        lines = ["%d\t%d\t%s" % (s, seq, safe(text)) if slot is None else "%d\t%s" % (seq, safe(text))
                 for _, s, seq, text in merge(per_slot)]
    else:
        text = read_bounded(share_path("logs", "clients", "slot-%d.log" % slot), MAX_LOG_BYTES)
        if text is None:
            raise Failure(EXIT_OBSERVATION, "logs/clients/slot-%d.log is not on the share" % slot)
        lines = [safe(line) for line in text.split("\n")]
        if lines and lines[-1] == "":
            lines.pop()
    if args.lines is not None:
        lines = lines[-args.lines:] if args.lines > 0 else []
    for line in lines:
        print(line)
    return 0


def cmd_cursor(args):
    print(json.dumps(take_cursor(load_clients()), ensure_ascii=True))
    return 0


class SlotWatch:
    """What counts as new in one slot's markers: after the cursor in the same generation and epoch."""

    def __init__(self, slot, base, any_generation):
        self.slot = slot
        self.base = base
        self.any_generation = any_generation
        self.generation = base.get("clientGeneration") if base else None
        self.epoch = base.get("observationEpoch") if base else None
        self.seq = (base.get("seq") or 0) if base else 0
        self.switched = base is None

    def new_records(self, header, records):
        if header is None:
            return []
        generation, epoch = header.get("clientGeneration"), header.get("observationEpoch")
        if generation != self.generation:
            if not self.switched:
                if not self.any_generation:
                    raise Failure(EXIT_OBSERVATION, "a new client took slot %d after the cursor (pass --any-generation to allow it)" % self.slot)
            self.generation, self.epoch, self.seq, self.switched = generation, epoch, 0, True
        elif self.epoch is None:
            if self.base is not None and self.base.get("running") is not True and not self.switched:
                # Not running and not read when the cursor was taken: its records are old.
                self.epoch, self.seq = epoch, max([seq for seq, _ in records] or [0])
                return []
            self.epoch = epoch
        elif epoch != self.epoch:
            raise Failure(EXIT_OBSERVATION,
                          "slot %d's log was read again from its start (the Bridge restarted or the file changed); take a new cursor" % self.slot)
        if header.get("catchingUp"):
            return []
        if (header.get("droppedThroughSeq") or 0) > self.seq:
            raise Failure(EXIT_OBSERVATION, "slot %d dropped markers written after the cursor (the markers file is full)" % self.slot)
        return [(seq, text) for seq, text in records if seq > self.seq]


def cmd_wait_marker(args):
    target = parse_slot(args.slot, allow_all=True)
    check_pattern(args.regex)
    clients = load_clients()
    cursor = parse_cursor(args.after) if args.after else {int(k): v for k, v in take_cursor(clients)["slots"].items()}
    if target is None:
        slots = sorted(s for s, v in cursor.items() if v.get("running") is True)
        if not slots:
            raise Failure(EXIT_OBSERVATION, "no slot had a running client when the cursor was taken")
    else:
        slots = [target]
    watches = {s: SlotWatch(s, cursor.get(s), args.any_generation) for s in slots}
    found = {}
    deadline = time.monotonic() + args.seconds
    warned_stale = False
    while True:
        clients = load_clients()
        if is_stale(clients) and not warned_stale:
            print("warning: status/vrchat-clients.json is older than 15 s; is the Bridge running?", file=sys.stderr)
            warned_stale = True
        for slot in slots:
            if slot in found:
                continue
            if logging_not_full(entry_of(clients, slot)):
                raise Failure(EXIT_OBSERVATION, "slot %d: VRChat's Logging is not Full, so its markers are not written" % slot)
            header, records = read_markers(slot)
            fresh = watches[slot].new_records(header, records)
            matches = search(args.regex, [text for _, text in fresh])
            if matches:
                found[slot] = fresh[matches[0]]
        if len(found) == len(slots):
            for slot in slots:
                seq, text = found[slot]
                print("%d\t%d\t%s" % (slot, seq, safe(text)))
            return 0
        if time.monotonic() >= deadline:
            missing = ", ".join(str(s) for s in slots if s not in found)
            print("timed out: no new marker matched in slot %s" % missing, file=sys.stderr)
            return EXIT_TIMEOUT
        time.sleep(POLL_SECONDS)


def cmd_wait_same_instance(args):
    clients = load_clients()
    cursor = parse_cursor(args.after) if args.after else None
    if args.slots:
        slots = [parse_slot(s.strip(), allow_all=False) for s in args.slots.split(",") if s.strip()]
    else:
        slots = sorted(c["slot"] for c in clients["clients"] if c.get("running") is True)
    if not slots:
        raise Failure(EXIT_OBSERVATION, "no slot has a running client")
    deadline = time.monotonic() + args.seconds
    while True:
        clients = load_clients()
        reasons = []
        instances = {}
        if is_stale(clients):
            reasons.append("status/vrchat-clients.json is older than 15 s")
        for slot in slots:
            c = entry_of(clients, slot)
            if c is None:
                reasons.append("slot %d has no client" % slot)
                continue
            if logging_not_full(c):
                raise Failure(EXIT_OBSERVATION, "slot %d: VRChat's Logging is not Full, so its state is not known" % slot)
            if c.get("running") is not True or c.get("state") != "joined" or c.get("catchingUp") or not c.get("instance"):
                reasons.append("slot %d is %s" % (slot, "catching up" if c.get("catchingUp") else (c.get("state") or "unknown")))
                continue
            base = cursor.get(slot) if cursor else None
            if base and base.get("clientGeneration") == c.get("clientGeneration"):
                if base.get("observationEpoch") is not None and base.get("observationEpoch") != c.get("observationEpoch"):
                    raise Failure(EXIT_OBSERVATION, "slot %d's log was read again from its start since the cursor; take a new cursor" % slot)
                if (c.get("joinedSeq") or 0) <= (base.get("seq") or 0):
                    reasons.append("slot %d has not joined again since the cursor" % slot)
                    continue
            instances[slot] = c["instance"]
        if not reasons and len(set(instances.values())) == 1:
            instance = next(iter(instances.values()))
            print("slots %s are in %s (the same instance is not necessarily the same build)"
                  % (",".join(str(s) for s in slots), safe(instance)))
            return 0
        if not reasons:
            reasons.append("instances differ: " + ", ".join("%d=%s" % (s, safe(i)) for s, i in sorted(instances.items())))
        if time.monotonic() >= deadline:
            print("timed out: " + "; ".join(reasons), file=sys.stderr)
            return EXIT_TIMEOUT
        time.sleep(POLL_SECONDS)


def seconds(value):
    try:
        result = float(value)
    except ValueError:
        raise argparse.ArgumentTypeError("not a number of seconds: %r" % value)
    if result < 0 or result > 24 * 3600:
        raise argparse.ArgumentTypeError("seconds must be between 0 and 86400")
    return result


class Parser(argparse.ArgumentParser):
    def error(self, message):
        self.print_usage(sys.stderr)
        print("vrcrt.sh: error: " + message, file=sys.stderr)
        sys.exit(EXIT_USAGE)


def main(argv):
    parser = Parser(prog="vrcrt.sh", description="Read the VRC Remote Test Bridge's per-client observation, "
                                                 "and look after the Bridge, over HTTPS.")
    sub = parser.add_subparsers(dest="command", parser_class=Parser)
    sub.required = True

    p = sub.add_parser("status", help="the clients in a table")
    p.add_argument("--json", action="store_true", help="print vrchat-clients.json as it is")
    p.set_defaults(run=cmd_status)

    p = sub.add_parser("result", help="read and verify a Bridge build result")
    p.add_argument("build_id")
    p.set_defaults(run=cmd_result)

    p = sub.add_parser("log", help="a slot's log, or the marker lines")
    p.add_argument("slot", help="0-3, or all with --markers")
    p.add_argument("--markers", action="store_true")
    p.add_argument("--lines", type=int)
    p.set_defaults(run=cmd_log)

    p = sub.add_parser("export-log", help="save verified full log snapshots for one or all running clients")
    p.add_argument("slot", help="0-3, or all running clients")
    p.add_argument("output_directory", help="existing local directory for the .log files")
    p.set_defaults(run=cmd_export_log)

    p = sub.add_parser("cursor", help="the current position, for --after")
    p.set_defaults(run=cmd_cursor)

    p = sub.add_parser("wait-marker", help="wait for a new marker line that matches a regex")
    p.add_argument("slot", help="0-3, or all: every slot running when the cursor was taken")
    p.add_argument("regex")
    p.add_argument("seconds", type=seconds)
    p.add_argument("--after")
    p.add_argument("--any-generation", action="store_true", help="also accept a new client in the slot")
    p.set_defaults(run=cmd_wait_marker)

    p = sub.add_parser("wait-same-instance", help="wait until the slots are joined in one instance")
    p.add_argument("seconds", type=seconds)
    p.add_argument("--slots")
    p.add_argument("--after")
    p.set_defaults(run=cmd_wait_same_instance)

    p = sub.add_parser("doctor", help="the Bridge's version, configuration, profiles, logins and log tail (v0.8.0+)")
    p.set_defaults(run=cmd_doctor)

    p = sub.add_parser("profiles", help="the account each VRChat profile last logged in to (v0.8.0+)")
    p.set_defaults(run=cmd_profiles)

    p = sub.add_parser("config", help="read or change the Bridge's editable settings (v0.8.0+)")
    config_sub = p.add_subparsers(dest="config_command", parser_class=Parser)
    config_sub.required = True
    c = config_sub.add_parser("get", help="the editable settings and their values, as JSON")
    c.set_defaults(run=cmd_config_get)
    c = config_sub.add_parser("set", help="change settings, then wait for the Bridge to restart and report")
    c.add_argument("assignments", nargs="+", metavar="KEY=VALUE",
                   help="VALUE is JSON when it parses (true, 3, [0,1]), else a string; KEY=null removes the key")
    c.set_defaults(run=cmd_config_set)

    p = sub.add_parser("restart", help="restart the Bridge host and wait for it (v0.8.0+)")
    p.set_defaults(run=cmd_restart)

    p = sub.add_parser("close-clients", help="close the VRChat clients the Bridge launched (v0.8.0+)")
    p.set_defaults(run=cmd_close_clients)

    args = parser.parse_args(argv)
    try:
        return args.run(args)
    except Failure as failure:
        print("vrcrt.sh: " + clip(failure), file=sys.stderr)
        return failure.code
    except KeyboardInterrupt:
        return 130
    except (TypeError, ValueError, KeyError, AttributeError, IndexError, OverflowError, RecursionError) as error:
        # An answer whose JSON is valid but not the shape the Bridge sends (a string where a
        # number belongs, say) and that no check above caught: an invalid answer, not a crash.
        print("vrcrt.sh: the Bridge's answer is not in the expected shape (%s: %s)"
              % (type(error).__name__, clip(error, MAX_CELL_CHARS)), file=sys.stderr)
        return EXIT_OBSERVATION


sys.exit(main(sys.argv[1:]))
PYTHON
