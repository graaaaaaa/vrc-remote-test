#!/usr/bin/env bash
# Checks scripts/vrcrt.sh against local protocol fixtures: the files are
# shaped like the ones the Bridge v0.6.0 writes (status/vrchat-clients.json and
# logs/clients/). Run from anywhere: bash scripts/test-vrcrt.sh
set -u

here="$(cd "$(dirname "$0")" && pwd)"
vrcrt="$here/vrcrt.sh"
share="$(mktemp -d "${TMPDIR:-/tmp}/vrcrt-test.XXXXXX")"
trap 'rm -rf "$share"' EXIT
export VRCRT_FIXTURE_DIR="$share"
mkdir -p "$share/status" "$share/logs/clients" "$share/results"

failures=0
pass() { printf 'ok   %s\n' "$1"; }
fail() { printf 'FAIL %s\n' "$1"; failures=$((failures + 1)); }

# expect <name> <exit code> <text the output must contain, or ""> <command...>
expect() {
    local name="$1" code="$2" text="$3"
    shift 3
    local output status
    output="$("$@" 2>&1)"
    status=$?
    if [ "$status" -ne "$code" ]; then
        fail "$name: exit $status, expected $code"
        printf '%s\n' "$output" | sed 's/^/     /'
    elif [ -n "$text" ] && ! printf '%s' "$output" | grep -qF -- "$text"; then
        fail "$name: output lacks '$text'"
        printf '%s\n' "$output" | sed 's/^/     /'
    else
        pass "$name"
    fi
}

now() { python3 -c 'from datetime import datetime, timezone; print(datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.1234567+00:00"))'; }

# clients <slot0 state> <slot1 state> [extra slot-1 JSON fields] [updatedAtUtc]
clients() {
    local updated="${4:-$(now)}"
    cat > "$share/status/vrchat-clients.json" <<EOF
{
  "schemaVersion": 1, "logMarkerPrefixes": ["[SPK003]"], "processListReadable": true, "unassignedClients": 0,
  "clients": [
    {"slot": 0, "clientGeneration": 11, "observationEpoch": 5, "processId": 100, "running": true,
     "state": "$1", "instance": "local:aaaa", "playersInInstance": ["A", "B"], "isMaster": true,
     "loggingLevel": "full", "authenticated": true, "joinedSeq": 40, "observedThroughSeq": 50, "catchingUp": false,
     "warnings": []},
    {"slot": 1, "clientGeneration": 22, "observationEpoch": 6, "processId": 200, "running": true,
     "state": "$2", "instance": "local:aaaa", "playersInInstance": ["A", "B"], "isMaster": false,
     "loggingLevel": "full", "authenticated": null, "joinedSeq": 30, "observedThroughSeq": 35, "catchingUp": false,
     "warnings": ["tab\there"] $3}
  ],
  "updatedAtUtc": "$updated"
}
EOF
}

# markers <slot> <generation> <epoch> <header extra> <records...>
markers() {
    local slot="$1" generation="$2" epoch="$3" extra="$4"
    shift 4
    {
        printf '# {"schemaVersion":1,"slot":%s,"clientGeneration":%s,"observationEpoch":%s,"catchingUp":false,"droppedRecords":0,"droppedThroughSeq":0%s}\n' \
            "$slot" "$generation" "$epoch" "$extra"
        for record in "$@"; do printf '%s\n' "$record"; done
    } > "$share/logs/clients/slot-$slot.markers.log"
}

H='2026.10.18 13:41:41 Debug      -  '

clients joined joined ""
markers 0 11 5 "" "10	${H}[SPK003] PLAYER_JOINED id=1" "45	2026.10.18 13:41:45 Debug      -  [SPK003] PLAYER_LEFT id=2"
markers 1 22 6 "" "12	2026.10.18 13:41:43 Debug      -  [SPK003] PLAYER_JOINED id=2"
printf '%s\n' "${H}line one" $'    continued\twith a tab\033[31m' > "$share/logs/clients/slot-1.log"

expect "status shows both slots" 0 "local:aaaa" "$vrcrt" status
expect "status escapes warnings" 0 'slot 1: tab\there' "$vrcrt" status
expect "status --json" 0 '"clientGeneration": 22' "$vrcrt" status --json
printf '%s\n' '{"buildId":"test-build","status":"deployed","sha256":"abc"}' > "$share/results/test-build.json"
expect "result confirms deployed build" 0 '"status": "deployed"' "$vrcrt" result test-build
expect "result rejects an unknown build" 3 "not available" "$vrcrt" result missing-build
expect "log escapes control characters" 0 'continued\twith a tab\x1b[31m' "$vrcrt" log 1
expect "log --lines" 0 "continued" "$vrcrt" log 1 --lines 1
expect "log all needs --markers" 2 "needs --markers" "$vrcrt" log all
expect "export-log rejects an invalid slot" 2 "a slot is 0 to 3" "$vrcrt" export-log 4 "$share"
expect "export-log needs an existing output directory" 2 "output directory does not exist" \
    "$vrcrt" export-log 0 "$share/missing"
expect "export-log rejects a client without a verified log source" 3 "no verified current log source" \
    "$vrcrt" export-log 0 "$share"
expect "a slot is 0 to 3" 2 "a slot is 0 to 3" "$vrcrt" log 4
expect "markers of one slot" 0 "45	2026.10.18 13:41:45" "$vrcrt" log 0 --markers

merged="$("$vrcrt" log all --markers)"
if [ "$(printf '%s\n' "$merged" | cut -f1,2 | tr '\t' ':' | tr '\n' ' ')" = "0:10 1:12 0:45 " ]; then
    pass "log all --markers merges by time"
else
    fail "log all --markers merges by time: $merged"
fi

cursor="$("$vrcrt" cursor)"
expect "cursor" 0 '"seq": 50' printf '%s' "$cursor"

expect "wait-marker sees nothing new before the cursor" 1 "timed out" \
    "$vrcrt" wait-marker 0 'PLAYER_LEFT' 0 --after "$cursor"

markers 0 11 5 "" "10	${H}[SPK003] PLAYER_JOINED id=1" "45	${H}[SPK003] PLAYER_LEFT id=2" "60	${H}[SPK003] PLAYER_RESTORED id=2"
expect "wait-marker finds a new marker" 0 "60	${H}[SPK003] PLAYER_RESTORED id=2" \
    "$vrcrt" wait-marker 0 'PLAYER_RESTORED id=\d' 2 --after "$cursor"
printf '%s' "$cursor" > "$share/cursor.json"
expect "wait-marker reads a cursor file" 0 "PLAYER_RESTORED" \
    "$vrcrt" wait-marker 0 'RESTORED' 2 --after "@$share/cursor.json"
expect "wait-marker all needs every slot" 1 "slot 1" \
    "$vrcrt" wait-marker all 'PLAYER_RESTORED' 0 --after "$cursor"

markers 0 11 7 "" "60	${H}[SPK003] PLAYER_RESTORED id=2"
expect "a new epoch is an error" 3 "read again from its start" \
    "$vrcrt" wait-marker 0 'RESTORED' 2 --after "$cursor"

markers 0 99 8 "" "3	${H}[SPK003] PLAYER_RESTORED id=2"
expect "a new generation is an error" 3 "--any-generation" \
    "$vrcrt" wait-marker 0 'RESTORED' 2 --after "$cursor"
expect "--any-generation accepts it" 0 "3	${H}[SPK003] PLAYER_RESTORED" \
    "$vrcrt" wait-marker 0 'RESTORED' 2 --after "$cursor" --any-generation

markers 0 11 5 ',"droppedThroughSeq":55' "60	${H}[SPK003] PLAYER_RESTORED id=2"
expect "dropped markers after the cursor are an error" 3 "dropped markers" \
    "$vrcrt" wait-marker 0 'RESTORED' 2 --after "$cursor"

expect "an invalid regex is a usage error" 2 "invalid regular expression" \
    "$vrcrt" wait-marker 0 '(' 1 --after "$cursor"
markers 0 11 5 "" "1	$(python3 -c 'print("a" * 60 + "b")')"
expect "a catastrophic regex is cut off after 2 s" 2 "took more than" \
    "$vrcrt" wait-marker 0 '(a|aa)+$' 3 --after '{"slots":{"0":{"clientGeneration":11,"observationEpoch":5,"seq":0,"running":true}}}'

expect "wait-same-instance" 0 "slots 0,1 are in local:aaaa" "$vrcrt" wait-same-instance 2
expect "wait-same-instance --after needs a new join" 1 "has not joined again" \
    "$vrcrt" wait-same-instance 0 --after "$cursor"
clients joined joining ""
expect "wait-same-instance waits for joined" 1 "slot 1 is joining" "$vrcrt" wait-same-instance 0 --slots 0,1
clients joined joined ', "loggingLevel": "errorsOnly"'
expect "Logging not Full is an error" 3 "Logging is not Full" "$vrcrt" wait-same-instance 2
clients joined joined "" "2026-10-18T00:00:00+00:00"
expect "an old client list is refused for a cursor" 3 "older than 15 s" "$vrcrt" cursor
expect "an old client list is warned about" 0 "warning:" "$vrcrt" status
clients joined joined ', "catchingUp": true'
expect "a client still being read has no cursor" 3 "catchingUp" "$vrcrt" cursor

rm "$share/status/vrchat-clients.json"
expect "without vrchat-clients.json" 3 "Bridge client status is unavailable" "$vrcrt" status

if [ "$failures" -ne 0 ]; then
    printf '%d check(s) failed\n' "$failures"
    exit 1
fi
printf 'all checks passed\n'
