#!/usr/bin/env bash
# Checks scripts/vrcrt.sh against local protocol fixtures: the files are
# shaped like the ones the Bridge v0.6.0 writes (status/vrchat-clients.json and
# logs/clients/). The last checks talk to a small HTTPS server on 127.0.0.1 instead, to
# exercise the real request code (they need openssl to make its certificate).
# Run from anywhere: bash scripts/test-vrcrt.sh
set -u

here="$(cd "$(dirname "$0")" && pwd)"
vrcrt="$here/vrcrt.sh"
share="$(mktemp -d "${TMPDIR:-/tmp}/vrcrt-test.XXXXXX")"
server=""
trap '[ -n "$server" ] && kill "$server" 2>/dev/null; rm -rf "$share"' EXIT
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

# --- Bridge v0.8.0 management commands: http/<METHOD>_api_v1_<route>[.n] stand in for the routes.
http_reset() { rm -rf "$share/http"; mkdir -p "$share/http"; }
# http <fixture name> [status] <body>
http() {
    if [ "$#" -eq 3 ]; then printf 'HTTP %s\n%s' "$2" "$3" > "$share/http/$1"; else printf '%s' "$2" > "$share/http/$1"; fi
}
# bridge_info <hostGeneration> [lastConfigApply JSON]
bridge_info() {
    printf '{"version":"0.8.0+abc","exeSha256":"00","processStartedAtUtc":"2026-10-30T00:00:00+00:00","hostGeneration":%s,' "$1"
    printf '"hostStartedAtUtc":"2026-10-30T00:00:01+00:00","listenUrl":"https://192.168.1.2:38443","certificateSha256":"ab",'
    printf '"activeConfigEtag":"etag1","savedConfigEtag":"etag1","activeConfig":{"RetainBuilds":10,"VrchatClientMode":"Relaunch\\u001b","VrchatProfiles":[0,1]},'
    printf '"paths":{"vrchatWorldsDirectory":{"display":"C:\\\\Users\\\\*\\\\Worlds","exists":true},"vrchatExecutable":{"display":"C:\\\\VRChat.exe","exists":false}},'
    printf '"lastConfigApply":%s,"busy":null,"restartPending":false,' "${2:-null}"
    printf '"configNotes":["AutoLaunchVrchat is false\\u001b[31m: a deploy installs the world but launches no VRChat."],'
    printf '"stateExportsBytes":1048576,"freeDiskBytes":53687091200}'
}
apply_report() { printf '{"operationId":"op1","requestedEtag":"etag2","outcome":"%s","problems":%s,"atUtc":"2026-10-30T00:00:05+00:00"}' "$1" "${2:-[]}"; }
PROFILES='{"configuredProfiles":[0,1],"profiles":[{"profile":0,"displayName":"Tester","idDigest":"0123456789ab","authenticatedAtUtc":"2026-10-30T00:00:02+00:00","slot":0,"clientGeneration":11},{"profile":1,"displayName":"Tester","idDigest":"0123456789ab","authenticatedAtUtc":"2026-10-30T00:00:03+00:00","slot":1,"clientGeneration":22}],"warnings":["Profiles 0 and 1 are logged in to the same account (Tester)."]}'
CONFIG='{"etag":"etag1","values":{"AutoLaunchVrchat":true},"active":{},"editable":[{"name":"AutoLaunchVrchat","kind":"boolean"}],"executableCandidates":[]}'

http_reset
clients joined joined ', "loginState": "authenticated", "account": {"displayName": "Tester\u202e", "idDigest": "0123456789ab", "authenticatedAtUtc": "2026-10-30T00:00:03+00:00"}'
http GET_api_v1_bridge "$(bridge_info 3 "$(apply_report rolledBack '["VrchatClientCount must be 1 to 4"]')")"
http GET_api_v1_profiles "$PROFILES"
printf '2026-10-30 00:00:01 info: started\n2026-10-30 00:00:02 warn: \033[31mred\n' > "$share/http/GET_api_v1_logs_bridge"
expect "doctor shows the version" 0 "Bridge        0.8.0+abc" "$vrcrt" doctor
expect "doctor shows the executable's SHA-256" 0 "Executable    SHA-256 00" "$vrcrt" doctor
expect "doctor shows the active configuration" 0 "  VrchatProfiles = [0, 1]" "$vrcrt" doctor
expect "doctor escapes the active configuration" 0 '  VrchatClientMode = Relaunch\x1b' "$vrcrt" doctor
expect "doctor escapes configuration notes" 0 'note: AutoLaunchVrchat is false\x1b[31m' "$vrcrt" doctor
expect "doctor shows the last config change" 0 "Last change   rolledBack" "$vrcrt" doctor
expect "doctor lists its problems" 0 "  - VrchatClientCount must be 1 to 4" "$vrcrt" doctor
expect "doctor marks a missing VRChat.exe" 0 'C:\VRChat.exe (missing)' "$vrcrt" doctor
expect "doctor shows profile warnings" 0 "warning: Profiles 0 and 1 are logged in to the same account" "$vrcrt" doctor
expect "doctor shows each client's login" 0 'authenticated  Tester\u202e' "$vrcrt" doctor
expect "doctor escapes the Bridge log" 0 '  2026-10-30 00:00:02 warn: \x1b[31mred' "$vrcrt" doctor
if grep -qF '"path": "/api/v1/logs/bridge?lines=30"' "$share/http/requests.log"; then
    pass "doctor asks for the last 30 log lines"
else
    fail "doctor asks for the last 30 log lines"
fi
clients joined joined ', "running": "yes\u001b[2J", "profile": "1\u001b"'
expect "doctor shows a running state that is not true/false as invalid" 0 \
    "1     invalid  invalid  -" "$vrcrt" doctor
rm "$share/http/GET_api_v1_bridge"
expect "doctor says when the Bridge is older than 0.8.0" 3 "older than v0.8.0" "$vrcrt" doctor
clients joined joined ""
expect "profiles" 0 "warning: Profiles 0 and 1" "$vrcrt" profiles
expect "profiles lists the configured ones" 0 "Configured profiles: 0,1" "$vrcrt" profiles
many_profiles="$(python3 -I -c '
import json
profiles = [{"profile": i % 100, "displayName": "N" * 1000 if i == 0 else "P%d" % i, "idDigest": "0123456789ab",
             "authenticatedAtUtc": "2026-10-30T00:00:02+00:00", "slot": 0, "clientGeneration": 1} for i in range(150)]
print(json.dumps({"configuredProfiles": [0], "profiles": profiles, "warnings": ["w"] * 80}))')"
http GET_api_v1_profiles "$many_profiles"
expect "profiles prints at most 100 rows" 0 "... and 50 more" "$vrcrt" profiles
expect "profiles cuts a long cell" 0 "$(printf 'N%.0s' $(seq 197))..." "$vrcrt" profiles
expect "profiles prints at most 50 warnings" 0 "warning: ... and 30 more" "$vrcrt" profiles
if [ "$("$vrcrt" profiles | wc -l)" -le 160 ]; then pass "profiles output is bounded"; else fail "profiles output is bounded"; fi
http GET_api_v1_profiles '{"configuredProfiles":null,"profiles":"abc","warnings":[]}'
expect "a profiles field that is not a list is an invalid answer" 3 "is not a list" "$vrcrt" profiles
http GET_api_v1_profiles "$(python3 -I -c 'print("[" * 100000 + "]" * 100000)')"
expect "deeply nested JSON is an invalid answer" 3 "invalid JSON" "$vrcrt" profiles
http GET_api_v1_profiles "$PROFILES"
clients joined joined ', "playersInInstance": 5'
expect "an answer of the wrong shape is exit 3, not a traceback" 3 "not in the expected shape" "$vrcrt" status
clients joined joined ""
http GET_api_v1_config "$CONFIG"
expect "config get" 0 '"etag": "etag1"' "$vrcrt" config get
expect "config needs get or set" 2 "" "$vrcrt" config
expect "config set needs KEY=VALUE" 2 "expected KEY=VALUE" "$vrcrt" config set AutoLaunchVrchat
expect "config set refuses a key given twice" 2 "more than once" "$vrcrt" config set RetainBuilds=3 RetainBuilds=4

http_reset
http GET_api_v1_config "$CONFIG"
http GET_api_v1_bridge.1 "$(bridge_info 1)"
http GET_api_v1_bridge.2 "DOWN"
http GET_api_v1_bridge.3 "$(bridge_info 2 "$(apply_report pending)")"
http GET_api_v1_bridge.4 "$(bridge_info 2 "$(apply_report applied)")"
http PUT_api_v1_config 202 '{"operationId":"op1","requestedEtag":"etag2","restart":"scheduled"}'
expect "config set waits for the restart and reports applied" 0 "outcome: applied" \
    "$vrcrt" config set AutoLaunchVrchat=false 'VrchatProfiles=[0,1]' VrchatMode=Desktop RetainBuilds=null LogMarkerPrefixes='["[A]"]'
if grep -F '"method": "PUT"' "$share/http/requests.log" | grep -qF '"If-Match": "\"etag1\""' \
    && grep -F '"method": "PUT"' "$share/http/requests.log" | grep -qF '"Content-Type": "application/json"' \
    && grep -qF '\"AutoLaunchVrchat\": false, \"VrchatProfiles\": [0, 1], \"VrchatMode\": \"Desktop\", \"RetainBuilds\": null' "$share/http/requests.log"; then
    pass "config set sends a merge patch with If-Match"
else
    fail "config set sends a merge patch with If-Match"
    sed 's/^/     /' "$share/http/requests.log"
fi

http_reset
http GET_api_v1_config "$CONFIG"
http GET_api_v1_bridge.1 "$(bridge_info 1)"
http GET_api_v1_bridge.2 "$(bridge_info 2 "$(apply_report rolledBack '["The new host did not start: VrchatExecutable does not exist."]')")"
http PUT_api_v1_config 202 '{"operationId":"op1","requestedEtag":"etag2","restart":"scheduled"}'
expect "config set reports a rollback" 1 "outcome: rolledBack" "$vrcrt" config set VrchatClientCount=3
expect "config set lists why" 1 "  - The new host did not start" "$vrcrt" config set VrchatClientCount=3

# The 90 s wait is a wall-clock bound: an answer that would arrive after it does not count.
http_reset
http GET_api_v1_config "$CONFIG"
http GET_api_v1_bridge.1 "$(bridge_info 1)"
http GET_api_v1_bridge.2 "$(printf 'SLOW 5\n%s' "$(bridge_info 2 "$(apply_report applied)")")"
http PUT_api_v1_config 202 '{"operationId":"op1","requestedEtag":"etag2","restart":"scheduled"}'
started=$(date +%s)
expect "config set stops waiting at its deadline, not after a slow answer" 1 "did not report operation op1" \
    env VRCRT_TEST_APPLY_TIMEOUT=1 VRCRT_TEST_POLL_SECONDS=0.2 "$vrcrt" config set AutoLaunchVrchat=true
if [ $(( $(date +%s) - started )) -le 3 ]; then pass "the deadline bounds the whole wait"; else fail "the deadline bounds the whole wait"; fi
http GET_api_v1_bridge.2 "$(printf 'SLOW 0.2\n%s' "$(bridge_info 2 "$(apply_report applied)")")"
expect "an answer within the deadline counts" 0 "outcome: applied" \
    env VRCRT_TEST_APPLY_TIMEOUT=2 VRCRT_TEST_POLL_SECONDS=0.1 "$vrcrt" config set AutoLaunchVrchat=true

http_reset
http GET_api_v1_config "$CONFIG"
http GET_api_v1_bridge "$(bridge_info 1)"
http PUT_api_v1_config 202 '{"operationId":"op1","requestedEtag":"etag2","restart":"scheduled"}'
expect "config set gives up when the Bridge never reports" 1 "did not report operation op1" \
    "$vrcrt" config set AutoLaunchVrchat=true
http PUT_api_v1_config 412 '{"error":"etagMismatch","etag":"etag9"}'
expect "config set reports a changed config.json" 1 "etagMismatch (etag9)" "$vrcrt" config set AutoLaunchVrchat=true
http PUT_api_v1_config 409 '{"error":"busy","operation":"deploy"}'
expect "config set reports a busy Bridge" 1 "busy (deploy)" "$vrcrt" config set AutoLaunchVrchat=true
http PUT_api_v1_config 422 '{"error":"invalidConfig","problems":["VrchatExecutable does not exist\u001b"]}'
expect "config set escapes the problems" 1 '  - VrchatExecutable does not exist\x1b' "$vrcrt" config set AutoLaunchVrchat=true

http_reset
http GET_api_v1_bridge.1 "$(bridge_info 4)"
http GET_api_v1_bridge.2 "DOWN"
http GET_api_v1_bridge.3 "$(bridge_info 5)"
http POST_api_v1_bridge_restart 202 '{"restart":"scheduled","hostGeneration":4}'
expect "restart waits for a new host" 0 "restarted: host generation 5" "$vrcrt" restart
http POST_api_v1_bridge_restart 429 '{"error":"tooSoon","retryAfterSeconds":30}'
expect "restart too soon" 1 "tooSoon (30)" "$vrcrt" restart
http POST_api_v1_clients_close '{"closed":2,"notConfirmed":0,"othersRunning":0,"problem":null}'
expect "close-clients" 0 "closed 2, not confirmed 0, others running 0" "$vrcrt" close-clients
http POST_api_v1_clients_close '{"closed":1,"notConfirmed":1,"othersRunning":0,"problem":null}'
expect "close-clients with a client not confirmed closed is not done" 1 "may still be running" "$vrcrt" close-clients
http POST_api_v1_clients_close '{"closed":"2","notConfirmed":0,"othersRunning":0,"problem":null}'
expect "close-clients with counts that are not numbers is an invalid answer" 3 "invalid counts" "$vrcrt" close-clients
http POST_api_v1_clients_close 409 '{"closed":0,"notConfirmed":0,"othersRunning":1,"problem":"A VRChat the Bridge did not launch is running."}'
expect "close-clients refused" 1 "problem: A VRChat the Bridge did not launch is running." "$vrcrt" close-clients
http POST_api_v1_clients_close DOWN
expect "close-clients without an answer" 3 "cannot reach the Bridge" "$vrcrt" close-clients

http POST_api_v1_clients_close '{}'
expect "close-clients with an answer that is not a close report is an invalid answer" 3 "not a close report" \
    "$vrcrt" close-clients
http POST_api_v1_clients_close 'not json'
expect "close-clients with an answer that is not JSON is an invalid answer" 3 "not a close report" \
    "$vrcrt" close-clients

clients joined joined ', "warnings": ['"$(python3 -I -c 'print(",".join(["\"w\""] * 80))')"']'
expect "status prints at most 50 warnings per slot" 0 "slot 1: ... and 30 more" "$vrcrt" status
clients joined joined ""
expect "a slot that is a digit but not 0-3 is a usage error" 2 "a slot is 0 to 3" \
    "$vrcrt" log "$(python3 -I -c 'print(chr(0xB2))')"
expect "a cursor with a wrong type is a usage error" 2 "is not a cursor" \
    "$vrcrt" wait-same-instance 0 --after '{"slots":{"0":{"seq":"x"}}}'

rm "$share/status/vrchat-clients.json"
expect "without vrchat-clients.json" 3 "Bridge client status is unavailable" "$vrcrt" status

# --- The real request code, against an HTTPS server on 127.0.0.1 with a throwaway certificate.
cat > "$share/server.py" <<'SERVER'
import hashlib, json, os, socket, ssl, sys, threading, time

directory = sys.argv[1]
context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.load_cert_chain(os.path.join(directory, "cert.pem"), os.path.join(directory, "key.pem"))
with open(os.path.join(directory, "cert.pem")) as stream:
    fingerprint = hashlib.sha256(ssl.PEM_cert_to_DER_cert(stream.read())).hexdigest()
listener = socket.socket()
listener.bind(("127.0.0.1", 0))
listener.listen(16)
counts, lock = {}, threading.Lock()


def answer(conn, status, body):
    # Content-Length and Connection: close, as Kestrel answers when it closes the connection.
    data = json.dumps(body).encode()
    conn.sendall(b"HTTP/1.1 %d X\r\nContent-Type: application/json\r\nContent-Length: %d\r\n"
                 b"Connection: close\r\n\r\n" % (status, len(data)) + data)


def trickle(conn):
    # A header one byte every 50 ms for up to 10 s: each socket wait is short, their sum is not.
    end = time.monotonic() + 10
    for byte in b"HTTP/1.1 200 OK\r\nX-Slow: " + b"a" * 1000:
        if time.monotonic() > end:
            break
        conn.sendall(bytes([byte]))
        time.sleep(0.05)


def handle(raw):
    try:
        conn = context.wrap_socket(raw, server_side=True)
        request = b""
        while b"\r\n\r\n" not in request:
            chunk = conn.recv(4096)
            if not chunk:
                return
            request += chunk
        method, path = request.split(b"\r\n", 1)[0].decode().split(" ")[:2]
        key = method + " " + path
        with lock:
            counts[key] = counts.get(key, 0) + 1
            n = counts[key]
        if key == "POST /api/v1/clients/close":
            answer(conn, 200, {"closed": 0, "notConfirmed": 0, "othersRunning": 0, "problem": None})
        elif key == "GET /api/v1/profiles":
            answer(conn, 200, {})
        elif key == "GET /api/v1/bridge" and n == 1:
            answer(conn, 200, {"version": "0.8.0", "hostGeneration": 1})
        elif key == "GET /api/v1/bridge":
            trickle(conn)
        elif key == "POST /api/v1/bridge/restart":
            answer(conn, 202, {"restart": "scheduled", "hostGeneration": 1})
        else:
            answer(conn, 404, {})
        conn.close()
    except (OSError, ValueError):
        pass


with open(os.path.join(directory, "server.tmp"), "w") as stream:
    stream.write("%d %s\n" % (listener.getsockname()[1], fingerprint))
os.rename(os.path.join(directory, "server.tmp"), os.path.join(directory, "server.ready"))
while True:
    raw, _ = listener.accept()
    threading.Thread(target=handle, args=(raw,), daemon=True).start()
SERVER

now_ms() { python3 -I -c 'import time; print(int(time.time() * 1000))'; }
if command -v openssl >/dev/null 2>&1 \
    && openssl req -x509 -newkey rsa:2048 -nodes -keyout "$share/key.pem" -out "$share/cert.pem" \
        -subj /CN=127.0.0.1 -days 1 >/dev/null 2>&1; then
    python3 -I "$share/server.py" "$share" &
    server=$!
    for _ in $(seq 50); do [ -f "$share/server.ready" ] && break; sleep 0.1; done
    if [ -f "$share/server.ready" ]; then
        read -r port fingerprint < "$share/server.ready"
        real() {
            env -u VRCRT_FIXTURE_DIR VRCRT_BRIDGE_URL="https://127.0.0.1:$port" VRCRT_CERT_SHA256="$fingerprint" \
                VRCRT_TOKEN=test-token "$@"
        }
        expect "a complete answer with Connection: close is read (no EBADF)" 0 "closed 0, not confirmed 0" \
            real "$vrcrt" close-clients
        expect "a 2-byte body with Connection: close is read" 0 "No profile has been seen" real "$vrcrt" profiles
        started=$(now_ms)
        expect "a trickling header cannot stretch the wait past its deadline" 1 "did not come back" \
            real env VRCRT_TEST_APPLY_TIMEOUT=1 VRCRT_TEST_POLL_SECONDS=0.1 "$vrcrt" restart
        elapsed=$(( $(now_ms) - started ))
        if [ "$elapsed" -le 2500 ]; then
            pass "the deadline holds against a trickling server (${elapsed} ms for a 1 s wait)"
        else
            fail "the deadline holds against a trickling server (${elapsed} ms for a 1 s wait)"
        fi
    else
        fail "the local HTTPS server did not start"
    fi
else
    printf 'skip the local HTTPS checks: openssl could not make a certificate\n'
fi

if [ "$failures" -ne 0 ]; then
    printf '%d check(s) failed\n' "$failures"
    exit 1
fi
printf 'all checks passed\n'
