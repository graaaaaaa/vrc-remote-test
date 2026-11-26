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
expect "markers as JSON" 0 '"seq": 45' "$vrcrt" log 0 --markers --json
expect "markers as JSON keep the header" 0 '"clientGeneration": 11' "$vrcrt" log 0 --markers --json
expect "markers JSON needs one slot" 2 "one slot's markers" "$vrcrt" log all --markers --json
expect "markers JSON needs --markers" 2 "one slot's markers" "$vrcrt" log 0 --json
expect "the packaged CLI runs directly" 0 "local:aaaa" bash "$here/../package/Tools~/vrcrt.sh" status
ln -s "$vrcrt" "$share/linked-vrcrt.sh"
expect "the scripts/ entry works through a link" 0 "local:aaaa" "$share/linked-vrcrt.sh" status
expect "the scripts/ entry passes the exit code" 2 "a slot is 0 to 3" "$share/linked-vrcrt.sh" log 9
expect "the scripts/ entry ignores an inherited CDPATH" 0 "local:aaaa" env CDPATH=. "$vrcrt" status
expect "export-log by generation refuses another client" 3 "now holds generation 11 epoch 5, not 12 epoch 5" \
    "$vrcrt" export-log 0 "$share" --generation 12 --epoch 5
expect "export-log by generation refuses another epoch" 3 "nothing exported" \
    "$vrcrt" export-log 0 "$share" --generation 11 --epoch 4
expect "export-log by generation still needs a verified log" 3 "no verified current log source" \
    "$vrcrt" export-log 0 "$share" --generation 11 --epoch 5
expect "export-log --generation needs --epoch" 2 "go together" "$vrcrt" export-log 0 "$share" --generation 11
expect "export-log --generation needs one slot" 2 "need one slot" "$vrcrt" export-log all "$share" --generation 11 --epoch 5

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

markers 0 11 5 "" "60	${H}[SPK003] vrcrt.build=abc [truncated]" "61	<line withheld: masking timed out>"
expect "markers JSON flags a cut record" 0 '"truncated": true' "$vrcrt" log 0 --markers --json
expect "markers JSON flags a withheld record" 0 '"withheld": true' "$vrcrt" log 0 --markers --json
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
expect "bridge --json" 0 '"hostGeneration": 3' "$vrcrt" bridge --json
expect "bridge --json keeps the process start" 0 '"processStartedAtUtc": "2026-10-30T00:00:00+00:00"' "$vrcrt" bridge --json
expect "bridge needs --json" 2 "needs --json" "$vrcrt" bridge
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

# One client (Bridge v0.10.0): POST /api/v1/clients/<slot>/close and /launch.
http_reset
http POST_api_v1_clients_0_close '{"slot":0,"processId":100,"closed":1,"notConfirmed":0,"recordUpdated":true,"problems":[]}'
expect "close-client" 0 "closed slot 0 (PID 100)" "$vrcrt" close-client 0
expect "close-client asks for that slot only" 0 "" grep -qF '"path": "/api/v1/clients/0/close"' "$share/http/requests.log"
http POST_api_v1_clients_0_close 409 '{"slot":0,"processId":100,"closed":0,"notConfirmed":1,"recordUpdated":false,"problems":["Could not confirm that the client exited\u001b"]}'
expect "close-client with the client not confirmed closed is not done" 1 'problem: Could not confirm that the client exited\x1b' \
    "$vrcrt" close-client 0
http POST_api_v1_clients_0_close 409 '{"slot":0,"processId":100,"closed":1,"notConfirmed":0,"recordUpdated":false,"problems":["The client exited, but the record could not be updated."]}'
expect "close-client with the record not updated is not done" 1 "closed slot 0 (PID 100)" "$vrcrt" close-client 0
http POST_api_v1_clients_0_close 409 '{"error":"notRunning","problems":["The VRChat client the Bridge launched in slot 0 (PID 100) is not running."]}'
expect "close-client refused" 1 "  - The VRChat client the Bridge launched in slot 0 (PID 100) is not running." "$vrcrt" close-client 0
http POST_api_v1_clients_0_close 409 '{"error":"busy","operation":"deploy"}'
expect "close-client while the Bridge is busy" 1 "busy (deploy)" "$vrcrt" close-client 0
http POST_api_v1_clients_0_close '{"slot":0,"processId":100,"closed":1,"notConfirmed":1,"recordUpdated":true,"problems":[]}'
expect "close-client with counts that do not add up is an invalid answer" 3 "not a close report" "$vrcrt" close-client 0
http POST_api_v1_clients_0_close '{"slot":1,"processId":100,"closed":1,"notConfirmed":0,"recordUpdated":true,"problems":[]}'
expect "close-client answered for another slot is an invalid answer" 3 "not a close report" "$vrcrt" close-client 0
http POST_api_v1_clients_0_close '{"slot":0,"processId":true,"closed":1,"notConfirmed":0,"recordUpdated":true,"problems":[]}'
expect "close-client with a PID that is not a number is an invalid answer" 3 "not a close report" "$vrcrt" close-client 0
http POST_api_v1_clients_0_close 409 '{"slot":0,"processId":100,"closed":1,"notConfirmed":0,"recordUpdated":true,"problems":[]}'
expect "close-client with a 409 that says done is an invalid answer" 3 "not a close report" "$vrcrt" close-client 0
http POST_api_v1_clients_0_close DOWN
expect "close-client without an answer says the outcome is unknown" 3 "whether slot 0 was closed is unknown" \
    "$vrcrt" close-client 0
rm "$share/http/POST_api_v1_clients_0_close"
expect "close-client on a Bridge older than v0.10.0" 3 "older than v0.10.0" "$vrcrt" close-client 0
http POST_api_v1_clients_0_close 401 ''
expect "close-client with a token the Bridge rejects" 3 "rejected the pairing token" "$vrcrt" close-client 0
expect "close-client with a slot out of range is a usage error" 2 "a slot is 0 to 3" "$vrcrt" close-client 4
expect "close-client with a slot like 01 is a usage error" 2 "a slot is 0 to 3" "$vrcrt" close-client 01

http POST_api_v1_clients_1_launch '{"slot":1,"profile":1,"processId":5678,"clientGeneration":4503599627370495}'
expect "launch-client" 0 "launched slot 1 (profile 1, PID 5678, generation 4503599627370495)" "$vrcrt" launch-client 1
http POST_api_v1_clients_1_launch '{"slot":1,"profile":1,"processId":5678,"clientGeneration":9007199254740993}'
expect "launch-client with a generation past 53 bits is an invalid answer" 3 "not a launch report" "$vrcrt" launch-client 1
http POST_api_v1_clients_1_launch '{"slot":1,"profile":false,"processId":5678,"clientGeneration":7}'
expect "launch-client with a profile that is not a number is an invalid answer" 3 "not a launch report" "$vrcrt" launch-client 1
http POST_api_v1_clients_1_launch 409 '{"error":"alreadyRunning","problems":["The VRChat client the Bridge launched in slot 1 is still running (PID 101)."]}'
expect "launch-client refused" 1 "alreadyRunning" "$vrcrt" launch-client 1
http POST_api_v1_clients_1_launch 409 '{"error":"noLaunchPlan","problems":["deploy a build first"]}'
expect "launch-client after a Bridge restart says to deploy" 1 "  - deploy a build first" "$vrcrt" launch-client 1
http POST_api_v1_clients_1_launch 400 '{"error":"unknownSlot","problems":["A client slot is 0 to 3."]}'
expect "launch-client with a slot the Bridge does not know" 1 "unknownSlot" "$vrcrt" launch-client 1
http POST_api_v1_clients_1_launch 500 '{"error":"launchedNotRecorded","slot":1,"launched":true,"recorded":false,"processId":5678,"problems":["Close it on Windows.\u001b[31m"]}'
expect "launch-client that started a client it could not record" 1 "VRChat started (PID 5678) but the Bridge could not record it" \
    "$vrcrt" launch-client 1
expect "launch-client escapes the problems" 1 '  - Close it on Windows.\x1b[31m' "$vrcrt" launch-client 1
http POST_api_v1_clients_1_launch 500 'Internal Server Error'
expect "launch-client with an answer it does not send" 3 "not one it sends" "$vrcrt" launch-client 1
http POST_api_v1_clients_1_launch "$(printf 'SLOW 0.5\n%s' '{"slot":1,"profile":1,"processId":5678,"clientGeneration":7}')"
expect "launch-client waits for a slow answer" 0 "launched slot 1" env VRCRT_TEST_LAUNCH_TIMEOUT=2 "$vrcrt" launch-client 1
http POST_api_v1_clients_1_launch "$(printf 'SLOW 3\n%s' '{"slot":1,"profile":1,"processId":5678,"clientGeneration":7}')"
expect "launch-client with no answer in time says the outcome is unknown" 3 "whether slot 1 was started is unknown" \
    env VRCRT_TEST_LAUNCH_TIMEOUT=0.5 "$vrcrt" launch-client 1
http POST_api_v1_clients_1_launch DOWN
expect "launch-client without an answer" 3 "cannot reach the Bridge" "$vrcrt" launch-client 1
rm "$share/http/POST_api_v1_clients_1_launch"
expect "launch-client on a Bridge older than v0.10.0" 3 "older than v0.10.0" "$vrcrt" launch-client 1

# --- Slot preconditions (Bridge v0.12.0, docs/plans/r1b-contract.md §2): close-client and
# launch-client with --expect @FILE [--operation-id ID] [--json]. requests.log records each request
# with its headers and body, so a check can tell what was sent, and that nothing was.
# logged <Python expression over `requests`, the list of logged requests, and `args`> [args...]:
# exit 0 when it is true.
logged() {
    python3 -I -c '
import json, os, sys
path = sys.argv[1]
requests = [json.loads(line) for line in open(path, encoding="utf-8")] if os.path.exists(path) else []
if eval(sys.argv[2], {"requests": requests, "args": sys.argv[3:]}):
    sys.exit(0)
print(json.dumps(requests, indent=1))
sys.exit(1)' "$share/http/requests.log" "$@"
}
# stdout_of <command...>: its standard output only (exit code ignored).
stdout_of() { "$@" 2>/dev/null; }
INSTANCE=0123456789abcdef0123456789abcdef
# expect_file <file name> <JSON>
expect_file() { printf '%s' "$2" > "$share/$1"; }
EXPECT_JSON='{"bridgeInstanceId":"'$INSTANCE'","hostGeneration":3,"activeConfigEtag":"etag1","savedConfigEtag":null,"operationRevision":17,"buildId":"20261126T010203-abc","clientGeneration":11}'
expect_file expect.json "$EXPECT_JSON"
CAPABILITIES='{"apiVersion":1,"buildProtocolVersion":1,"maxArtifactSizeBytes":524288000,"maxClients":4,"features":["slotPreconditions.v1"]}'
CLOSE_R1B='{"slot":0,"processId":100,"closed":1,"notConfirmed":0,"recordUpdated":true,"problems":[],"clientGeneration":11,"operationRevision":18,"bridgeInstanceId":"'$INSTANCE'","operationId":"run1-close"}'
LAUNCH_R1B='{"slot":0,"profile":0,"processId":300,"clientGeneration":12,"operationRevision":19,"bridgeInstanceId":"'$INSTANCE'","operationId":null}'
CURRENT='{"bridgeInstanceId":"'$INSTANCE'","hostGeneration":3,"activeConfigEtag":"etag1","savedConfigEtag":null,"operationRevision":18,"buildId":"20261126T010203-abc","clientGeneration":null}'

# Without --expect: the same request (no body, no Content-Type, no capabilities read), line and exit
# code as before, while the answer carries the fields R1b adds (Commander's dice_life_auto).
http_reset
http POST_api_v1_clients_0_close "$CLOSE_R1B"
http POST_api_v1_clients_0_launch "$LAUNCH_R1B"
if [ "$(stdout_of "$vrcrt" close-client 0; echo "exit $?")" = "$(printf 'closed slot 0 (PID 100)\nexit 0')" ]; then
    pass "close-client without --expect prints the same line for an answer with the R1b fields"
else
    fail "close-client without --expect prints the same line for an answer with the R1b fields"
fi
if [ "$(stdout_of "$vrcrt" launch-client 0; echo "exit $?")" = "$(printf 'launched slot 0 (profile 0, PID 300, generation 12)\nexit 0')" ]; then
    pass "launch-client without --expect prints the same line for an answer with the R1b fields"
else
    fail "launch-client without --expect prints the same line for an answer with the R1b fields"
fi
expect "close-client and launch-client without --expect send no body and read no capabilities" 0 "" logged \
    '[(r["method"], r["path"], r["body"], r["headers"]) for r in requests] == [("POST", "/api/v1/clients/0/close", None, {}), ("POST", "/api/v1/clients/0/launch", None, {})]'
http_reset
http POST_api_v1_clients_0_close "$CLOSE_R1B"
http POST_api_v1_clients_0_launch "$LAUNCH_R1B"
expect "close-client --json without --expect prints the answer" 0 '"operationRevision": 18' "$vrcrt" close-client 0 --json
expect "close-client --json prints JSON only on stdout" 0 "" python3 -I -c '
import json, sys
reply = json.loads(sys.argv[1])
sys.exit(0 if reply["clientGeneration"] == 11 and reply["bridgeInstanceId"] == sys.argv[2] else 1)' \
    "$(stdout_of "$vrcrt" close-client 0 --json)" "$INSTANCE"
expect "launch-client --json without --expect prints the answer" 0 '"clientGeneration": 12' "$vrcrt" launch-client 0 --json
expect "launch-client --json prints no line" 0 "" python3 -I -c 'import json, sys; json.loads(sys.argv[1])' \
    "$(stdout_of "$vrcrt" launch-client 0 --json)"
expect "--json without --expect sends no body either" 0 "" logged 'all(r["body"] is None and r["method"] == "POST" for r in requests)'
http POST_api_v1_clients_0_close 409 '{"slot":0,"processId":100,"closed":0,"notConfirmed":1,"recordUpdated":false,"problems":["Could not confirm"],"clientGeneration":11,"operationRevision":18,"bridgeInstanceId":"'$INSTANCE'","operationId":null}'
expect "close-client not confirmed, with the R1b fields, is still not done" 1 "slot 0 (PID 100) may still be running" \
    "$vrcrt" close-client 0
expect "close-client --json not confirmed prints the answer and is still not done" 1 '"notConfirmed": 1' \
    "$vrcrt" close-client 0 --json
http POST_api_v1_clients_0_launch 500 '{"error":"launchedNotRecorded","slot":0,"launched":true,"recorded":false,"processId":300,"problems":["Close it on Windows."],"operationRevision":19,"bridgeInstanceId":"'$INSTANCE'","operationId":null}'
expect "launch-client launchedNotRecorded with the R1b fields is still exit 1" 1 "could not record it" "$vrcrt" launch-client 0
http POST_api_v1_clients_0_close '{"slot":0,"processId":100,"closed":1,"notConfirmed":0,"recordUpdated":true,"problems":[],"operationRevision":"18"}'
expect "close-client with an operationRevision that is not a number is an invalid answer" 3 "not a close report" \
    "$vrcrt" close-client 0
http POST_api_v1_clients_0_close '{"slot":0,"processId":100,"closed":1,"notConfirmed":0,"recordUpdated":true,"problems":[],"clientGeneration":true}'
expect "close-client with a clientGeneration that is true is an invalid answer" 3 "not a close report" \
    "$vrcrt" close-client 0
http POST_api_v1_clients_0_launch '{"slot":0,"profile":0,"processId":300,"clientGeneration":12,"bridgeInstanceId":"0123456789ABCDEF0123456789ABCDEF"}'
expect "launch-client with a bridgeInstanceId of the wrong form is an invalid answer" 3 "not a launch report" \
    "$vrcrt" launch-client 0
http POST_api_v1_clients_0_launch '{"slot":0,"profile":0,"processId":300,"clientGeneration":12,"operationId":"a b"}'
expect "launch-client with an operationId of the wrong form is an invalid answer" 3 "not a launch report" \
    "$vrcrt" launch-client 0

http_reset
http POST_api_v1_clients_0_close "$CLOSE_R1B"
expect "--operation-id without --expect is a usage error" 2 "--operation-id goes with --expect" \
    "$vrcrt" close-client 0 --operation-id run1-close
expect "--operation-id of the wrong form is a usage error" 2 "--operation-id is 1 to 64" \
    "$vrcrt" launch-client 0 --expect "@$share/expect.json" --operation-id 'run 1'
expect "a usage error sends nothing" 0 "" logged 'requests == []'

# With --expect: capabilities first, then one POST with the exact body as application/json.
http_reset
http GET_api_v1_capabilities "$CAPABILITIES"
http POST_api_v1_clients_0_close "$CLOSE_R1B"
http POST_api_v1_clients_0_launch "$LAUNCH_R1B"
if [ "$(stdout_of "$vrcrt" close-client 0 --expect "@$share/expect.json" --operation-id run1-close; echo "exit $?")" \
    = "$(printf 'closed slot 0 (PID 100)\nexit 0')" ]; then
    pass "close-client --expect done prints the same line"
else
    fail "close-client --expect done prints the same line"
fi
expect "close-client --expect reads capabilities, then sends the expect object as JSON" 0 "" logged \
    '([(r["method"], r["path"]) for r in requests] == [("GET", "/api/v1/capabilities"), ("POST", "/api/v1/clients/0/close")]
      and requests[1]["headers"] == {"Content-Type": "application/json"} and requests[1]["body"] == args[0])' \
    '{"expect":'"$EXPECT_JSON"',"operationId":"run1-close"}'
http_reset
http GET_api_v1_capabilities "$CAPABILITIES"
http POST_api_v1_clients_0_launch "$LAUNCH_R1B"
expect "launch-client --expect --json prints the answer" 0 '"operationRevision": 19' \
    "$vrcrt" launch-client 0 --expect "@$share/expect.json" --json
expect "launch-client --expect without --operation-id sends no operationId" 0 "" logged \
    'requests[-1]["path"] == "/api/v1/clients/0/launch" and requests[-1]["body"] == args[0]' '{"expect":'"$EXPECT_JSON"'}'
expect_file expect-spaced.json '{
  "clientGeneration": 1, "buildId": null, "operationRevision": 0, "savedConfigEtag": "'"$(printf 'e%.0s' $(seq 128))"'",
  "activeConfigEtag": null, "hostGeneration": 2147483647, "bridgeInstanceId": "'$INSTANCE'"
}'
expect "an expect file with null etag and buildId, edge values and its own key order is accepted" 0 "launched slot 0" \
    "$vrcrt" launch-client 0 --expect "@$share/expect-spaced.json"

# An expect file of the wrong shape: exit 2, and nothing is asked of the Bridge, not even capabilities.
bad_expect() {
    local name="$1" json="$2" text="$3"
    http_reset
    http GET_api_v1_capabilities "$CAPABILITIES"
    http POST_api_v1_clients_0_close "$CLOSE_R1B"
    expect_file bad.json "$json"
    expect "an expect file with $name is a usage error" 2 "$text" "$vrcrt" close-client 0 --expect "@$share/bad.json"
    expect "an expect file with $name sends nothing" 0 "" logged 'requests == []'
}
with() { python3 -I -c 'import json, sys; e = json.loads(sys.argv[1]); exec(sys.argv[2]); print(json.dumps(e))' "$EXPECT_JSON" "$1"; }
bad_expect "a missing key" "$(with 'del e["buildId"]')" "buildId is missing"
bad_expect "an extra key" "$(with 'e["operationId"] = "x"')" '"operationId" is not one of the seven fields'
bad_expect "a duplicate key" "${EXPECT_JSON%\}},\"operationRevision\":17}" '"operationRevision" appears twice'
bad_expect "a hostGeneration that is a string" "$(with 'e["hostGeneration"] = "3"')" "hostGeneration must be an integer from 1"
bad_expect "a hostGeneration past 32 bits" "$(with 'e["hostGeneration"] = 2 ** 31')" "hostGeneration must be an integer from 1"
bad_expect "an empty etag" "$(with 'e["activeConfigEtag"] = ""')" "activeConfigEtag must be a string of 1 to 128"
bad_expect "a hostGeneration that is true" "$(with 'e["hostGeneration"] = True')" "hostGeneration must be an integer"
bad_expect "a clientGeneration that is 0" "$(with 'e["clientGeneration"] = 0')" "clientGeneration must be an integer of at least 1"
bad_expect "an operationRevision that is a float" "$(with 'e["operationRevision"] = 17.0')" "operationRevision must be an integer of at least 0"
bad_expect "an operationRevision past 64 bits" "$(with 'e["operationRevision"] = 2 ** 63')" "operationRevision must be"
bad_expect "an operationRevision of NaN" "$(with 'e["operationRevision"] = float("nan")')" "NaN is not a JSON number"
bad_expect "a clientGeneration of Infinity" "$(with 'e["clientGeneration"] = float("inf")')" "Infinity is not a JSON number"
bad_expect "a bridgeInstanceId in upper case" "$(with 'e["bridgeInstanceId"] = e["bridgeInstanceId"].upper()')" \
    "bridgeInstanceId must be 32 lowercase hexadecimal digits"
bad_expect "a bridgeInstanceId of 31 digits" "$(with 'e["bridgeInstanceId"] = e["bridgeInstanceId"][1:]')" "bridgeInstanceId must be"
bad_expect "a bridgeInstanceId with a newline" "$(with 'e["bridgeInstanceId"] += "\n"')" "bridgeInstanceId must be"
bad_expect "a buildId with a slash" "$(with 'e["buildId"] = "../x"')" "buildId must be"
bad_expect "an etag of 129 characters" "$(with 'e["activeConfigEtag"] = "e" * 129')" "activeConfigEtag must be"
bad_expect "an etag that is a number" "$(with 'e["savedConfigEtag"] = 1')" "savedConfigEtag must be"
bad_expect "an array instead of an object" "[$EXPECT_JSON]" "does not hold a JSON object"
bad_expect "the whole request body instead of the expect object" "{\"expect\":$EXPECT_JSON}" '"expect" is not one of the seven fields'
bad_expect "text that is not JSON" "not json" "not valid JSON"
bad_expect "more than 4096 bytes" "$(with 'e["pad"] = "x" * 5000')" "larger than 4096 bytes"
http_reset
expect "--expect without @ is a usage error" 2 "--expect needs @FILE" "$vrcrt" close-client 0 --expect "$EXPECT_JSON"
expect "--expect with a missing file is a usage error" 2 "cannot read the --expect file" \
    "$vrcrt" launch-client 0 --expect "@$share/missing.json"
expect "a usage error of --expect sends nothing" 0 "" logged 'requests == []'

# A Bridge that does not announce slotPreconditions.v1 (v0.11 or older) never gets the POST: it would
# ignore the body and act without checking anything.
no_feature() {
    local name="$1" command="$2"
    expect "$command --expect on a Bridge $name is unsupported" 3 "unsupported" \
        "$vrcrt" "$command" 0 --expect "@$share/expect.json"
    expect "$command --expect on a Bridge $name sends no POST" 0 "" logged 'all(r["method"] == "GET" for r in requests)'
}
for command in close-client launch-client; do
    http_reset
    http POST_api_v1_clients_0_close "$CLOSE_R1B"
    http POST_api_v1_clients_0_launch "$LAUNCH_R1B"
    http GET_api_v1_capabilities '{"apiVersion":1,"buildProtocolVersion":1,"maxArtifactSizeBytes":524288000,"maxClients":4}'
    no_feature "without features" "$command"
    http GET_api_v1_capabilities '{"apiVersion":1,"features":"slotPreconditions.v1"}'
    no_feature "whose features are not a list" "$command"
    http GET_api_v1_capabilities '{"apiVersion":1,"features":["slotPreconditions.v2"]}'
    no_feature "with other features only" "$command"
    http GET_api_v1_capabilities 'not json'
    no_feature "whose capabilities are not JSON" "$command"
    http GET_api_v1_capabilities 500 '{"error":"internal"}'
    no_feature "whose capabilities fail" "$command"
    http GET_api_v1_capabilities DOWN
    no_feature "that does not answer" "$command"
    rm "$share/http/GET_api_v1_capabilities"
    no_feature "without capabilities" "$command"
done

# 412: the Bridge did nothing because something changed since the expect file was written.
http_reset
http GET_api_v1_capabilities "$CAPABILITIES"
http POST_api_v1_clients_0_close 412 '{"error":"preconditionFailed","mismatched":["operationRevision","buildId"],"current":'"$CURRENT"',"operationId":"run1-close"}'
expect "close-client --expect with a precondition failed is exit 1" 1 "precondition failed: operationRevision,buildId" \
    "$vrcrt" close-client 0 --expect "@$share/expect.json" --operation-id run1-close
expect "a failed precondition is never sent again" 0 "" logged \
    'sum(r["method"] == "POST" for r in requests) == 1'
expect "close-client --expect --json with a precondition failed prints the 412 body" 1 '"error": "preconditionFailed"' \
    "$vrcrt" close-client 0 --expect "@$share/expect.json" --json
expect "the 412 body on stdout is JSON" 0 "" python3 -I -c '
import json, sys
reply = json.loads(sys.argv[1])
sys.exit(0 if reply["mismatched"] == ["operationRevision", "buildId"] and reply["current"]["operationRevision"] == 18 else 1)' \
    "$(stdout_of "$vrcrt" close-client 0 --expect "@$share/expect.json" --json)"
http POST_api_v1_clients_0_launch 412 '{"error":"preconditionFailed","mismatched":["clientGeneration"],"current":'"$CURRENT"',"operationId":null}'
expect "launch-client --expect with a precondition failed is exit 1" 1 "precondition failed: clientGeneration" \
    "$vrcrt" launch-client 0 --expect "@$share/expect.json"
http POST_api_v1_clients_0_launch 412 '{"error":"preconditionFailed","mismatched":["\u001b[2J"],"current":{}}'
expect "a 412 naming a field that is not one of the seven is an invalid answer" 3 "not one it sends (HTTP 412)" \
    "$vrcrt" launch-client 0 --expect "@$share/expect.json"
http POST_api_v1_clients_0_launch 412 '{"error":"preconditionFailed","mismatched":[],"current":{}}'
expect "a 412 naming nothing is an invalid answer" 3 "not one it sends (HTTP 412)" \
    "$vrcrt" launch-client 0 --expect "@$share/expect.json"
http POST_api_v1_clients_0_close 412 '{"error":"preconditionFailed","mismatched":["operationRevision"],"current":'"$CURRENT"',"operationId":null}'
expect "a 412 without --expect is still an answer the Bridge does not send" 3 "not one it sends" "$vrcrt" close-client 0

# 400 invalidPrecondition: the Bridge refused the body before doing anything.
http POST_api_v1_clients_0_close 400 '{"error":"invalidPrecondition","problems":["expect.clientGeneration is missing.\u001b"]}'
expect "close-client --expect refused as invalidPrecondition is exit 2" 2 '  - expect.clientGeneration is missing.\x1b' \
    "$vrcrt" close-client 0 --expect "@$share/expect.json"
http POST_api_v1_clients_0_launch 400 '{"error":"invalidPrecondition","problems":["The body is not valid JSON."]}'
expect "launch-client --expect refused as invalidPrecondition is exit 2" 2 "invalidPrecondition" \
    "$vrcrt" launch-client 0 --expect "@$share/expect.json"
expect "invalidPrecondition without --expect is read as before (a refusal, exit 1)" 1 "invalidPrecondition" \
    "$vrcrt" launch-client 0

# Every other answer to a request with --expect: the exit code it has without --expect.
http POST_api_v1_clients_0_close 409 '{"error":"busy","operation":"deploy"}'
expect "close-client --expect while the Bridge is busy" 1 "busy (deploy)" "$vrcrt" close-client 0 --expect "@$share/expect.json"
http POST_api_v1_clients_0_close 409 '{"error":"notRunning","problems":["not running"]}'
expect "close-client --expect refused" 1 "notRunning" "$vrcrt" close-client 0 --expect "@$share/expect.json"
http POST_api_v1_clients_0_launch 500 '{"error":"launchedNotRecorded","slot":0,"launched":true,"recorded":false,"processId":300,"problems":["Close it on Windows."],"operationRevision":19,"bridgeInstanceId":"'$INSTANCE'","operationId":"run1-launch"}'
expect "launch-client --expect --json launchedNotRecorded prints the answer, exit 1" 1 '"operationId": "run1-launch"' \
    "$vrcrt" launch-client 0 --expect "@$share/expect.json" --operation-id run1-launch --json
http_reset
http GET_api_v1_capabilities "$CAPABILITIES"
http POST_api_v1_clients_0_launch DOWN
expect "launch-client --expect without an answer says the outcome is unknown" 3 "whether slot 0 was started is unknown" \
    "$vrcrt" launch-client 0 --expect "@$share/expect.json"
http POST_api_v1_clients_0_close "$(printf 'SLOW 3\n%s' "$CLOSE_R1B")"
expect "close-client --expect with no answer in time says the outcome is unknown" 3 "whether slot 0 was closed is unknown" \
    env VRCRT_TEST_CLOSE_TIMEOUT=0.5 "$vrcrt" close-client 0 --expect "@$share/expect.json"
expect "a request with --expect that got no answer is not sent again" 0 "" logged \
    '[r["path"] for r in requests if r["method"] == "POST"] == ["/api/v1/clients/0/launch", "/api/v1/clients/0/close"]'

# bridge --json passes the v0.12.0 fields through (and still works without them, above).
http_reset
http GET_api_v1_bridge "$(bridge_info 3 | python3 -I -c '
import json, sys
info = json.load(sys.stdin)
info.update(bridgeInstanceId=sys.argv[1], operationRevision=17,
            currentBatch={"buildId": "20261126T010203-abc", "launchedSlots": [0, 1]})
print(json.dumps(info))' "$INSTANCE")"
expect "bridge --json keeps bridgeInstanceId" 0 "\"bridgeInstanceId\": \"$INSTANCE\"" "$vrcrt" bridge --json
expect "bridge --json keeps operationRevision and currentBatch" 0 "" python3 -I -c '
import json, sys
info = json.loads(sys.argv[1])
sys.exit(0 if info["operationRevision"] == 17 and info["currentBatch"] == {"buildId": "20261126T010203-abc", "launchedSlots": [0, 1]} else 1)' \
    "$(stdout_of "$vrcrt" bridge --json)"
http GET_api_v1_bridge "$(bridge_info 3 | sed 's/}$/,"currentBatch":null}/')"
expect "bridge --json keeps a currentBatch of null" 0 '"currentBatch": null' "$vrcrt" bridge --json

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
        if key == "GET /api/v1/capabilities":
            answer(conn, 200, {"apiVersion": 1, "features": ["slotPreconditions.v1"]})
        elif key == "POST /api/v1/clients/2/close":
            # Records what arrived (Content-Type and the body by its Content-Length) for the test to compare.
            head, _, body = request.partition(b"\r\n\r\n")
            headers = dict(line.split(b":", 1) for line in head.split(b"\r\n")[1:] if b":" in line)
            headers = {name.strip().lower().decode(): value.strip().decode() for name, value in headers.items()}
            length = int(headers.get("content-length", "0"))
            while len(body) < length:
                chunk = conn.recv(4096)
                if not chunk:
                    break
                body += chunk
            with open(os.path.join(directory, "received.json"), "w") as stream:
                json.dump({"contentType": headers.get("content-type"), "body": body.decode()}, stream)
            answer(conn, 200, {"slot": 2, "processId": 300, "closed": 1, "notConfirmed": 0, "recordUpdated": True,
                               "problems": [], "clientGeneration": 11, "operationRevision": 18, "operationId": "net1"})
        elif key == "POST /api/v1/clients/close":
            answer(conn, 200, {"closed": 0, "notConfirmed": 0, "othersRunning": 0, "problem": None})
        elif key == "GET /api/v1/profiles":
            answer(conn, 200, {})
        elif key == "GET /api/v1/bridge" and n == 1:
            answer(conn, 200, {"version": "0.8.0", "hostGeneration": 1})
        elif key in ("GET /api/v1/bridge", "POST /api/v1/clients/0/launch", "POST /api/v1/clients/0/close"):
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
        for command in launch-client close-client; do
            started=$(now_ms)
            expect "$command against a trickling header says the outcome is unknown" 3 "is unknown" \
                real env VRCRT_TEST_LAUNCH_TIMEOUT=1 VRCRT_TEST_CLOSE_TIMEOUT=1 "$vrcrt" "$command" 0
            elapsed=$(( $(now_ms) - started ))
            if [ "$elapsed" -le 2500 ]; then
                pass "$command's deadline holds against a trickling server (${elapsed} ms for a 1 s wait)"
            else
                fail "$command's deadline holds against a trickling server (${elapsed} ms for a 1 s wait)"
            fi
        done
        expect "close-client --expect over HTTPS sends the body" 0 "closed slot 2 (PID 300)" \
            real "$vrcrt" close-client 2 --expect "@$share/expect.json" --operation-id net1
        expect "the body arrives whole as application/json" 0 "" python3 -I -c '
import json, sys
received = json.load(open(sys.argv[1]))
sys.exit(0 if received == {"contentType": "application/json", "body": sys.argv[2]} else 1)' \
            "$share/received.json" '{"expect":'"$EXPECT_JSON"',"operationId":"net1"}'
    else
        fail "the local HTTPS server did not start"
    fi
else
    printf 'skip the local HTTPS checks: openssl could not make a certificate\n'
fi

# The runner has its own checks; run them here too so one command covers the Mac side.
printf -- '--- vrcrt_runner.py\n'
bash "$here/test-vrcrt-runner.sh" || failures=$((failures + 1))
printf -- '--- vrcrt-run.py\n'
bash "$here/test-vrcrt-run.sh" || failures=$((failures + 1))

if [ "$failures" -ne 0 ]; then
    printf '%d check(s) failed\n' "$failures"
    exit 1
fi
printf 'all checks passed\n'
