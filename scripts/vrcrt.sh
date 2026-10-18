#!/usr/bin/env bash
# VRC Remote Test: read what the Windows Bridge (v0.6.0 and later) observes about each
# VRChat client, from the share mounted on the Mac (client-observation plan F7).
#
# Read-only: it reads status/vrchat-clients.json and logs/clients/ on the share and never
# writes there. Builds and deploys stay with Unity (batch mode included).
#
#   vrcrt.sh status [--json]
#   vrcrt.sh log <slot> [--markers] [--lines N]
#   vrcrt.sh log all --markers [--lines N]
#   vrcrt.sh cursor
#   vrcrt.sh wait-marker <slot|all> <regex> <seconds> [--after <cursor>] [--any-generation]
#   vrcrt.sh wait-same-instance <seconds> [--slots 0,1] [--after <cursor>]
#
# The share is $VRCRT_SHARE (default /Volumes/VRCRemoteTest). <cursor> is the JSON that
# `cursor` prints, or @<file> holding it. Exit codes: 0 done, 1 timed out, 2 usage error,
# 3 the observation cannot answer (old or missing files, a client still being read, the
# Bridge restarted or re-read a log, Logging not Full, records dropped).
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
import json
import os
import re
import subprocess
import sys
import time
from datetime import datetime, timedelta, timezone

SHARE = os.environ.get("VRCRT_SHARE", "/Volumes/VRCRemoteTest")
SLOTS = range(4)
STALE_AFTER = timedelta(seconds=15)
REGEX_TIMEOUT_SECONDS = 2.0
MAX_PATTERN_LENGTH = 1000
MAX_CLIENTS_BYTES = 256 * 1024
MAX_LOG_BYTES = 512 * 1024
MAX_MARKERS_BYTES = 1280 * 1024
POLL_SECONDS = 1.0

EXIT_TIMEOUT = 1
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
    return os.path.join(SHARE, *parts)


def read_bounded(path, limit):
    """The file's text, or None when it does not exist. Larger than the limit is an observation failure."""
    try:
        with open(path, "rb") as stream:
            data = stream.read(limit + 1)
    except FileNotFoundError:
        return None
    except OSError as error:
        raise Failure(EXIT_OBSERVATION, "cannot read %s: %s" % (path, error.strerror or error))
    if len(data) > limit:
        raise Failure(EXIT_OBSERVATION, "%s is larger than %d bytes; not reading it" % (path, limit))
    return data.decode("utf-8", errors="replace")


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
                "status/vrchat-clients.json is not on the share %s (is it mounted, and is the Bridge v0.6.0 or later?)" % SHARE)
        return None
    try:
        data = json.loads(text)
    except ValueError as error:
        raise Failure(EXIT_OBSERVATION, "status/vrchat-clients.json is not valid JSON (%s)" % error)
    if not isinstance(data, dict) or not isinstance(data.get("clients"), list):
        raise Failure(EXIT_OBSERVATION, "status/vrchat-clients.json has no clients list")
    data["clients"] = [c for c in data["clients"] if isinstance(c, dict) and c.get("slot") in SLOTS]
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
        except ValueError:
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
    except ValueError:
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
    widths = [max(len(str(row[i])) for row in rows) for i in range(len(rows[0]))]
    for row in rows:
        print("  ".join(str(cell).ljust(width) for cell, width in zip(row, widths)).rstrip())


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
    parser = Parser(prog="vrcrt.sh", description="Read the VRC Remote Test Bridge's per-client observation from the share.")
    sub = parser.add_subparsers(dest="command", parser_class=Parser)
    sub.required = True

    p = sub.add_parser("status", help="the clients in a table")
    p.add_argument("--json", action="store_true", help="print vrchat-clients.json as it is")
    p.set_defaults(run=cmd_status)

    p = sub.add_parser("log", help="a slot's log, or the marker lines")
    p.add_argument("slot", help="0-3, or all with --markers")
    p.add_argument("--markers", action="store_true")
    p.add_argument("--lines", type=int)
    p.set_defaults(run=cmd_log)

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

    args = parser.parse_args(argv)
    try:
        return args.run(args)
    except Failure as failure:
        print("vrcrt.sh: " + safe(str(failure)), file=sys.stderr)
        return failure.code
    except KeyboardInterrupt:
        return 130


sys.exit(main(sys.argv[1:]))
PYTHON
