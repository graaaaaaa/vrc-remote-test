#!/usr/bin/env bash
# Checks package/Tools~/vrcrt_runner.py with a stand-in for vrcrt.sh: a small program that
# answers status, bridge, cursor, log --markers --json, export-log, close-client and
# launch-client from files under $VRCRT_FIXTURE_DIR/fake, which each check writes, and logs every
# call. Run from anywhere: bash scripts/test-vrcrt-runner.sh
set -u

here="$(CDPATH= cd -P -- "$(dirname -- "$0")" && pwd)"
runner="$here/../package/Tools~/vrcrt_runner.py"
work="$(mktemp -d "${TMPDIR:-/tmp}/vrcrt-runner-test.XXXXXX")"
# Background answerers of the manual-steps checks may still be polling at the end.
trap 'kill $(jobs -p) 2>/dev/null; wait 2>/dev/null; rm -rf "$work"' EXIT
export VRCRT_FIXTURE_DIR="$work/fixture"
export VRCRT_RUNNER_TEST_CLI="$work/fake-cli.sh"
export VRCRT_RUNNER_TEST_POLL=0.05
export VRCRT_RUNNER_TEST_READY=2
export VRCRT_RUNNER_TEST_TAG=1
export VRCRT_RUNNER_TEST_LEAVE=2
export VRCRT_RUNNER_TEST_REJOIN=2
export VRCRT_BRIDGE_URL="https://192.0.2.1:38443"
export VRCRT_CERT_SHA256="$(printf 'a%.0s' $(seq 64))"
fake="$VRCRT_FIXTURE_DIR/fake"
mkdir -p "$fake"

failures=0
pass() { printf 'ok   %s\n' "$1"; }
fail() { printf 'FAIL %s\n' "$1"; failures=$((failures + 1)); }

cat > "$work/fake.py" <<'FAKE'
import json, os, sys, time
from datetime import datetime, timezone
d = os.path.join(os.environ["VRCRT_FIXTURE_DIR"], "fake")
args = sys.argv[1:]
with open(os.path.join(d, "calls.log"), "a") as log:
    log.write(" ".join(args) + "\n")
with open(os.path.join(d, "env.log"), "a") as log:
    log.write(json.dumps(dict(os.environ)) + "\n")
opid = args[args.index("--operation-id") + 1] if "--operation-id" in args else ""
def fill(data):
    return data.replace("__NOW__", datetime.now(timezone.utc).isoformat()).replace("__OPID__", opid)
# answer(name[, then]): prints the file (SLEEP s / EXIT n first lines: wait, or exit n with the
# first line on stderr and the rest on stdout); `then`, when it exists, replaces the file afterwards.
def answer(name, then=None):
    path = os.path.join(d, name)
    if not os.path.exists(path):
        sys.stderr.write("fake: no " + name + "\n"); sys.exit(3)
    data = open(path).read()
    if then and os.path.exists(os.path.join(d, then)):
        os.replace(os.path.join(d, then), path)
    if data.startswith("SLEEP "):
        time.sleep(float(data.split()[1])); data = data.split("\n", 1)[1]
    if data.startswith("EXIT "):
        first, _, rest = data.partition("\n")
        sys.stderr.write(first + "\n"); sys.stdout.write(fill(rest)); sys.exit(int(first.split()[1]))
    sys.stdout.write(fill(data))
    sys.exit(0)
command = args[0]
if command == "status":
    answer("clients.json")
elif command == "bridge":
    # A third answer: once bridge-then.json is used up, bridge-later.json becomes the next one.
    later, then = os.path.join(d, "bridge-later.json"), os.path.join(d, "bridge-then.json")
    if os.path.exists(later) and not os.path.exists(then):
        os.replace(later, then)
    answer("bridge.json", "bridge-then.json")
elif command in ("close-client", "launch-client"):
    # Keeps what --expect named, moves the staged <name>-after-<kind>.json files in, then answers <kind>.json.
    kind = command.split("-")[0]
    if "--expect" in args:
        with open(args[args.index("--expect") + 1][1:]) as source, open(os.path.join(d, "expect-%s.seen" % kind), "w") as out:
            out.write(source.read())
    for name in ("clients", "bridge", "markers-0", "markers-1"):
        staged = os.path.join(d, "%s-after-%s.json" % (name, kind))
        if os.path.exists(staged):
            os.replace(staged, os.path.join(d, name + ".json"))
    answer(kind + ".json")
elif command == "cursor":
    answer("cursor.json")
elif command == "log":
    answer("markers-%s.json" % args[1])
elif command == "export-log":
    slot, folder, generation, epoch = args[1], args[2], args[4], args[6]
    path = os.path.join(folder, "vrchat-slot-%s-generation-%s-epoch-%s-0123456789abcdef0123456789abcdef.log" % (slot, generation, epoch))
    with open(path, "w") as out:
        out.write("full log of slot %s\n" % slot)
    print(path)
else:
    sys.stderr.write("fake: unknown command\n"); sys.exit(2)
FAKE
printf '#!/usr/bin/env bash\nexec python3 -I "%s" "$@"\n' "$work/fake.py" > "$work/fake-cli.sh"

# bridge <processStartedAtUtc> [freeDiskBytes] [mode]
# FEATURE=1 adds the 0.12 fields (slotPreconditions.v1): bridgeInstanceId, operationRevision REV
# (default 17) and currentBatch with buildId BATCH; BRIDGE_OUT names another file to write.
bridge() {
    {
        printf '{"version":"0.11.0","exeSha256":"%s","processStartedAtUtc":"%s","hostGeneration":2,"activeConfigEtag":"e1","savedConfigEtag":"e1",' \
            "$(printf 'b%.0s' $(seq 64))" "${1:-2026-11-26T00:00:00+00:00}"
        [ -n "${FEATURE:-}" ] && printf '"bridgeInstanceId":"%s","operationRevision":%s,"currentBatch":{"buildId":"%s","launchedSlots":[0,1]},' \
            "$instance_id" "${REV:-17}" "${BATCH:-20261126T000001000Z-33334444}"
        printf '"activeConfig":{"VrchatClientMode":"%s","VrchatProfiles":[0,1],"VrchatMode":"Desktop"},"freeDiskBytes":%s,"stateExportsBytes":1024}' \
            "${3:-${BRIDGE_MODE:-Relaunch}}" "${2:-53687091200}"
    } > "$fake/${BRIDGE_OUT:-bridge.json}"
}
instance_id=0123456789abcdef0123456789abcdef
# client <slot> <generation> <epoch> <joinedSeq> <state> <inErrorWorld> <loginState> [instance]
# JOIN_ATTEMPT=<seq> adds joinAttemptSeq (the join's Joining line); without it the field is absent.
client() {
    printf '{"slot":%s,"clientGeneration":%s,"observationEpoch":%s,"processId":%s,"running":%s,"profile":%s,"state":"%s",' \
        "$1" "$2" "$3" "$((100 + $1))" "${RUNNING:-true}" "$1" "$5"
    [ -n "${JOIN_ATTEMPT:-}" ] && printf '"joinAttemptSeq":%s,' "$JOIN_ATTEMPT"
    printf '"instance":"%s","inErrorWorld":%s,"loggingLevel":"full","logStatus":"shown","loginState":"%s","joinedSeq":%s,"observedThroughSeq":50,"catchingUp":false}' \
        "${8:-local:inst}" "$6" "$7" "$4"
}
# clients <updatedAtUtc> <entry> <entry>
clients() {
    printf '{"schemaVersion":1,"processListReadable":true,"clients":[%s,%s],"updatedAtUtc":"%s"}' "$2" "$3" "$1" > "$fake/${CLIENTS_OUT:-clients.json}"
}
cursor() { printf '{"schemaVersion":1,"slots":{"0":{"clientGeneration":11,"observationEpoch":%s,"seq":%s,"running":true},"1":{"clientGeneration":22,"observationEpoch":6,"seq":50,"running":true}}}' \
    "${CURSOR_EPOCH0:-5}" "${CURSOR_SEQ0:-50}" > "$fake/cursor.json"; }
# markers <slot> <generation> <epoch> <records JSON> [droppedThroughSeq]
markers() {
    printf '{"schemaVersion":1,"slot":%s,"header":{"clientGeneration":%s,"observationEpoch":%s,"catchingUp":false,"droppedThroughSeq":%s},"records":%s}' \
        "$1" "$2" "$3" "${5:-0}" "$4" > "$fake/${MARKERS_OUT:-markers-$1.json}"
}
tagged() { printf '[{"seq":5,"text":"2026.11.26 00:00:00 Debug - [VRCRT] PRT001 vrcrt.build=%s ev=LOAD","truncated":%s,"withheld":false}]' "$1" "${2:-false}"; }

ready_clients() {
    clients "__NOW__" "$(client 0 111 7 4 joined false authenticated)" "$(client 1 222 8 4 joined false authenticated)"
    markers 0 111 7 "$(tagged "${1:-RUNTAG}")"
    markers 1 222 8 "$(tagged "${1:-RUNTAG}")"
}
before_clients() {
    clients "__NOW__" "$(client 0 11 5 40 joined false authenticated)" "$(client 1 22 6 40 joined false authenticated)"
}
# deploy_result <runDir> <deploy> <launch> <stopReason JSON> <tag JSON>
deploy_result() {
    printf '{"schema":"vrcrt.deploy-result","schemaVersion":1,"attemptId":"20261126T000000000Z-11112222","buildId":"20261126T000001000Z-33334444",' > "$1/deploy-result.json"
    printf '"artifact":{"sha256":null,"size":1,"worldBuildTag":%s},"classification":{"deploy":"%s","launch":"%s","stopReason":%s}}' \
        "$5" "$2" "$3" "$4" >> "$1/deploy-result.json"
}
field() { python3 -I -c 'import json,sys; v=json.load(open(sys.argv[1]))
for k in sys.argv[2].split("."): v = v.get(k) if isinstance(v, dict) else None
print(v)' "$1" "$2"; }

root="$work/root"
# prepare_run [extra prepare args...]: sets $run and $token
prepare_run() {
    rm -f "$root"/locks/*.json "$fake"/*-after-*.json "$fake"/bridge-then.json "$fake"/bridge-later.json "$fake"/close.json "$fake"/launch.json \
        "$fake"/expect-*.seen
    bridge; before_clients; cursor
    local line
    line="$(python3 -I "$runner" prepare --slots 0,1 --owner-pid $$ --run-root "$root" "$@")" || return $?
    run="$(printf '%s' "$line" | python3 -I -c 'import json,sys; print(json.load(sys.stdin)["runDir"])')"
    token="$(printf '%s' "$line" | python3 -I -c 'import json,sys; print(json.load(sys.stdin)["claimToken"])')"
    tag="$(printf '%s' "$line" | python3 -I -c 'import json,sys; print(json.load(sys.stdin)["worldBuildTag"])')"
}
# check <name> <expected exit> <run.json field> <expected value>
check() {
    local name="$1" code="$2" key="$3" want="$4" status got
    python3 -I "$runner" continue --run "$run" --claim-token "$token" > "$work/out" 2>&1
    status=$?
    got="$(field "$run/run.json" "$key")"
    if [ "$status" -ne "$code" ] || [ "$got" != "$want" ]; then
        fail "$name: exit $status (want $code), $key=$got (want $want)"; sed 's/^/     /' "$work/out"
    else
        pass "$name"
    fi
}

# --- prepare
prepare_run
if [ -d "$run" ] && [ "$(field "$run/run.json" state)" = "prepared" ] && [ -f "$run/before.cursor.json" ]; then
    pass "prepare saves the state before the deploy"
else
    fail "prepare saves the state before the deploy"
fi
[ "$(stat -f %Lp "$run")" = "700" ] && pass "the run folder is owner-only" || fail "the run folder is owner-only"
python3 -I "$runner" prepare --slots 0,1 --owner-pid $$ --run-root "$root" > "$work/out" 2>&1
[ $? -eq 3 ] && grep -q otherRunActive "$work/out" && pass "a second prepare on the same Bridge is refused" \
    || { fail "a second prepare on the same Bridge is refused"; cat "$work/out"; }

# --- the whole procedure
ready_clients "$tag"
deploy_result "$run" PASS PASS null "\"$tag\""
check "deploy-collect passes with every client ready and tagged" 0 result PASS
[ "$(field "$run/run.json" results.worldBuildMatch)" = "PASS" ] && pass "world build matches" || fail "world build matches"
[ "$(field "$run/run.json" results.testVerdict)" = "notJudged" ] && pass "the test itself is not judged" || fail "the test itself is not judged"
ls "$run"/evidence/slot-0/g111-e7/vrchat-slot-0-*.log > /dev/null 2>&1 && ls "$run"/evidence/slot-1/g222-e8/markers.json > /dev/null 2>&1 \
    && pass "evidence is kept per slot, generation and epoch" || fail "evidence is kept per slot, generation and epoch"
ls "$root"/locks/*.json > /dev/null 2>&1 && fail "the claim is released at the end" || pass "the claim is released at the end"
[ "$(stat -f %Lp "$run/progress.jsonl")" = "600" ] && [ "$(stat -f %Lp "$run/run.json")" = "600" ] \
    && pass "run files are owner-only" || fail "run files are owner-only"

prepare_run; ready_clients OTHERTAG1; deploy_result "$run" PASS PASS null "\"$tag\""
check "another build's tag fails the run" 1 results.worldBuildMatch FAIL

prepare_run; ready_clients "$tag"; deploy_result "$run" PASS PASS null "\"$tag\""
markers 1 222 8 "[{\"seq\":5,\"text\":\"[VRCRT] vrcrt.build=$tag\",\"truncated\":false,\"withheld\":false},{\"seq\":7,\"text\":\"[VRCRT] vrcrt.build=OTHERTAG1\",\"truncated\":false,\"withheld\":false}]"
check "a later other tag fails even after this one" 1 results.worldBuildMatch FAIL
prepare_run; ready_clients "$tag"; deploy_result "$run" PASS PASS null null
check "no tag for these bytes leaves the build match unknown" 0 results.worldBuildMatch UNKNOWN

# VRChat logs a world's Start() markers between Joining (joinAttemptSeq) and Finished entering world (joinedSeq).
prepare_run; deploy_result "$run" PASS PASS null "\"$tag\""
clients "__NOW__" "$(JOIN_ATTEMPT=2 client 0 111 7 9 joined false authenticated)" "$(JOIN_ATTEMPT=2 client 1 222 8 9 joined false authenticated)"
markers 0 111 7 "$(tagged "$tag")"; markers 1 222 8 "$(tagged "$tag")"
check "a tag logged while the world loads (before the join finished) matches" 0 results.worldBuildMatch PASS
prepare_run; deploy_result "$run" PASS PASS null "\"$tag\""
clients "__NOW__" "$(JOIN_ATTEMPT=6 client 0 111 7 9 joined false authenticated)" "$(JOIN_ATTEMPT=6 client 1 222 8 9 joined false authenticated)"
markers 0 111 7 "$(tagged OTHERTAG1)"; markers 1 222 8 "$(tagged OTHERTAG1)"
check "a tag from before this join's Joining line is not counted" 0 results.worldBuildMatch UNKNOWN
prepare_run; deploy_result "$run" PASS PASS null "\"$tag\""
clients "__NOW__" "$(client 0 111 7 9 joined false authenticated)" "$(client 1 222 8 9 joined false authenticated)"
markers 0 111 7 "$(tagged "$tag")"; markers 1 222 8 "$(tagged "$tag")"
check "without joinAttemptSeq only lines after the join count" 0 results.worldBuildMatch UNKNOWN

# --world-build-wait: 0 looks once; out of range is a usage error.
prepare_run --world-build-wait 0; ready_clients "$tag"; deploy_result "$run" PASS PASS null "\"$tag\""
check "--world-build-wait 0 still matches a tag that is already there" 0 results.worldBuildMatch PASS
[ "$(field "$run/run.json" procedure.parameters.worldBuildWaitSeconds)" = "0" ] && pass "the wait is recorded with the procedure" \
    || fail "the wait is recorded with the procedure"
prepare_run --world-build-wait 0; ready_clients "$tag"; markers 0 111 7 "[]"; markers 1 222 8 "[]"
deploy_result "$run" PASS PASS null "\"$tag\""
check "--world-build-wait 0 without a tag leaves the match unknown" 0 results.worldBuildMatch UNKNOWN
python3 -I -c '
import json, sys
from datetime import datetime
op = [o for o in json.load(open(sys.argv[1]))["operations"] if o["name"] == "waitWorldBuild"][0]
span = datetime.fromisoformat(op["finishedAtUtc"]) - datetime.fromisoformat(op["startedAtUtc"])
sys.exit(0 if span.total_seconds() < 0.5 else 1)' "$run/run.json" \
    && pass "--world-build-wait 0 does not wait" || fail "--world-build-wait 0 does not wait"
rm -f "$root"/locks/*.json
python3 -I "$runner" prepare --slots 0,1 --owner-pid $$ --run-root "$root" --world-build-wait 601 > "$work/out" 2>&1
[ $? -eq 2 ] && pass "--world-build-wait over 600 is refused" || { fail "--world-build-wait over 600 is refused"; cat "$work/out"; }

prepare_run; ready_clients "$tag"; markers 0 111 7 "$(tagged "$tag" true)"; deploy_result "$run" PASS PASS null "\"$tag\""
check "a cut tag line proves nothing" 0 results.worldBuildMatch UNKNOWN

prepare_run; ready_clients "$tag"; markers 0 111 7 "$(tagged "$tag")" 9; deploy_result "$run" PASS PASS null "\"$tag\""
check "dropped markers leave the build match unknown" 0 results.worldBuildMatch UNKNOWN

# --- the deploy result decides whether to go on
prepare_run; ready_clients "$tag"; deploy_result "$run" UNKNOWN UNKNOWN '"deployedButLaunch:partial"' null
check "a contradictory deploy result stops UNKNOWN" 3 result UNKNOWN
prepare_run; ready_clients "$tag"; deploy_result "$run" FAIL FAIL '"partialLaunch"' null
check "a real partial start fails the readiness" 1 results.clientReadiness FAIL
prepare_run; ready_clients "$tag"; deploy_result "$run" PASS notApplicable '"autoLaunchDisabled"' null
check "AutoLaunch off stops UNKNOWN" 3 reason "launch:autoLaunchDisabled"
prepare_run; ready_clients "$tag"
check "no deploy result is UNKNOWN" 3 reason noDeployResult
prepare_run; ready_clients "$tag"
printf '{"schema":"vrcrt.deploy-result","schemaVersion":1,"artifact":[1],"classification":{"deploy":"PASS"}}' > "$run/deploy-result.json"
check "a deploy result missing its launch verdict is UNKNOWN" 3 reason deployResultInvalid

# --- readiness
prepare_run; deploy_result "$run" PASS PASS null null
clients "__NOW__" "$(client 0 111 7 4 joined false authenticated)" "$(client 1 222 8 4 joined true authenticated)"
check "a client in the error world fails after the wait" 1 reason notReady
prepare_run; deploy_result "$run" PASS PASS null null
clients "__NOW__" "$(client 0 111 7 4 joined false authenticated)" "$(client 1 222 8 null starting null likelyLoggedOut)"
check "a likely logged-out client stops UNKNOWN" 3 reason likelyLoggedOut
grep -q "profile 1 on Windows" "$run/run.json" && pass "it says which profile to log in again" || fail "it says which profile to log in again"
prepare_run; deploy_result "$run" PASS PASS null null
clients "2026-01-01T00:00:00+00:00" "$(client 0 111 7 4 joined false authenticated)" "$(client 1 222 8 4 joined false authenticated)"
check "a stale client list is UNKNOWN, not FAIL" 3 results.clientReadiness UNKNOWN
prepare_run; deploy_result "$run" PASS PASS null null
clients "__NOW__" "$(client 0 111 7 4 joined false authenticated)" "$(client 1 222 8 4 joined false authenticated local:other)"
check "clients in different instances are not ready" 1 reason notReady
BRIDGE_MODE=Reload prepare_run; deploy_result "$run" PASS PASS null null
BRIDGE_MODE=Reload bridge
clients "__NOW__" "$(client 0 11 9 60 joined false authenticated)" "$(client 1 222 8 4 joined false authenticated)"
check "Reload: the same client read again from its start is UNKNOWN" 3 reason epochChangedSinceDeploy
CURSOR_EPOCH0=null CURSOR_SEQ0=0 BRIDGE_MODE=Reload prepare_run; deploy_result "$run" PASS PASS null null
BRIDGE_MODE=Reload bridge
clients "__NOW__" "$(client 0 11 5 4 joined false authenticated)" "$(client 1 222 8 4 joined false authenticated)"
check "Reload: a client whose log was not read before the deploy is UNKNOWN" 3 reason baselineUnknown
BRIDGE_MODE=Reload prepare_run; deploy_result "$run" PASS PASS null null
BRIDGE_MODE=Reload bridge
clients "__NOW__" "$(client 0 11 5 60 joined false authenticated)" "$(client 1 22 6 60 joined false authenticated)"
markers 0 11 5 "[]"; markers 1 22 6 "[]"
check "Reload: a join after the cursor in the same generation is ready" 0 results.clientReadiness PASS
BRIDGE_MODE=Reload prepare_run; deploy_result "$run" PASS PASS null null
BRIDGE_MODE=Reload bridge
clients "__NOW__" "$(JOIN_ATTEMPT=45 client 0 11 5 60 joined false authenticated)" "$(JOIN_ATTEMPT=55 client 1 22 6 60 joined false authenticated)"
markers 0 11 5 "[]"; markers 1 22 6 "[]"
check "Reload: a join that began before the deploy is not ready" 1 reason notReady
prepare_run; deploy_result "$run" PASS PASS null null
clients "__NOW__" "$(client 0 11 5 60 joined false authenticated)" "$(client 1 222 8 4 joined false authenticated)"
check "Relaunch: a client that was not relaunched is not ready" 1 reason notReady
prepare_run; deploy_result "$run" PASS PASS null null
clients "__NOW__" "$(client 0 111 7 4 joined false authenticated | sed 's/"logStatus":"shown"/"logStatus":"notYet"/')" "$(client 1 222 8 4 joined false authenticated)"
check "a client whose log the Bridge has not matched yet is UNKNOWN" 3 results.clientReadiness UNKNOWN
prepare_run; deploy_result "$run" PASS PASS null null
clients "__NOW__" "$(client 0 111 null null unknown null unknown)" "$(client 1 222 8 4 joined false authenticated)"
check "a client whose log is not read yet is UNKNOWN, not FAIL" 3 results.clientReadiness UNKNOWN
prepare_run; deploy_result "$run" PASS PASS null null
clients "__NOW__" "$(client 0 11 5 40 joined false authenticated)" "$(client 1 222 8 4 joined false authenticated)"
check "a client that did not join after the deploy is not ready" 1 reason notReady

# --- what changes on the way
prepare_run; ready_clients "$tag"; deploy_result "$run" PASS PASS null "\"$tag\""
bridge "2026-11-26T01:00:00+00:00"
check "a restarted Bridge is UNKNOWN" 3 reason bridgeChanged
prepare_run; ready_clients "$tag"; deploy_result "$run" PASS PASS null "\"$tag\""
bridge "2026-11-26T00:00:00+00:00" 1024
check "low Windows disk stops before exporting" 3 reason windowsDiskLow
prepare_run; ready_clients "$tag"; deploy_result "$run" PASS PASS null "\"$tag\""
VRCRT_RUNNER_TEST_MAC_FREE=4611686018427387904 check "low Mac disk stops before writing" 3 reason macDiskLow

# --- cancel, claims, recovery
prepare_run; deploy_result "$run" PASS PASS null null
clients "__NOW__" "$(client 0 111 7 4 joined false authenticated)" "$(client 1 222 8 4 joined true authenticated)"
( sleep 0.5; touch "$run/cancel.request" ) &
start=$(python3 -I -c 'import time; print(time.monotonic())')
check "cancel.request ends the wait as CANCELED" 4 result CANCELED
elapsed=$(python3 -I -c "import time; print(int(time.monotonic() - $start))")
[ "$elapsed" -lt 2 ] && pass "the cancellation is seen within a second" || fail "the cancellation is seen within a second (${elapsed} s)"
prepare_run; ready_clients "$tag"; deploy_result "$run" PASS PASS null null
printf 'SLEEP 30\n%s' "$(cat "$fake/clients.json")" > "$fake/clients.json"
( sleep 0.5; touch "$run/cancel.request" ) &
start=$(python3 -I -c 'import time; print(time.monotonic())')
check "cancelling stops a CLI child that hangs" 4 result CANCELED
elapsed=$(python3 -I -c "import time; print(int(time.monotonic() - $start))")
[ "$elapsed" -lt 8 ] && pass "the hung child is stopped and reaped quickly" || fail "the hung child is stopped and reaped quickly (${elapsed} s)"
prepare_run; ready_clients "$tag"; deploy_result "$run" PASS PASS null "\"$tag\""
python3 -I - "$fake/markers-0.json" "$tag" <<'PY'
import json, sys
records = [{"seq": n, "text": "2026.11.26 00:00:00 Debug - [VRCRT] FLOOD n=%d" % n, "truncated": False, "withheld": False}
           for n in range(1, 10001)]
records.append({"seq": 10001, "text": "[VRCRT] vrcrt.build=%s" % sys.argv[2], "truncated": False, "withheld": False})
json.dump({"schemaVersion": 1, "slot": 0, "header": {"clientGeneration": 111, "observationEpoch": 7, "catchingUp": False,
           "droppedThroughSeq": 0}, "records": records}, open(sys.argv[1], "w"))
PY
start=$(python3 -I -c 'import time; print(time.monotonic())')
check "10,000 marker records are read in bounded time" 0 results.worldBuildMatch PASS
elapsed=$(python3 -I -c "import time; print(int(time.monotonic() - $start))")
[ "$elapsed" -lt 10 ] && pass "the flood does not stretch the run (${elapsed} s)" || fail "the flood does not stretch the run (${elapsed} s)"
VRCRT_RUNNER_TEST_RUN=1 prepare_run; deploy_result "$run" PASS PASS null null
clients "__NOW__" "$(client 0 111 7 4 joined false authenticated)" "$(client 1 222 8 4 joined true authenticated)"
sleep 1.1
check "the run's own deadline, fixed at prepare, stops it UNKNOWN" 3 reason runDeadline
prepare_run; ready_clients "$tag"; deploy_result "$run" PASS PASS null null
good="$token"; token=00000000000000000000000000000000
check "continue with another claim token changes nothing" 3 state prepared
token="$good"
python3 -I "$runner" recover --run "$run" > "$work/out" 2>&1
[ $? -eq 3 ] && grep -q runActive "$work/out" && [ "$(field "$run/run.json" state)" = prepared ] \
    && pass "recover refuses a run whose owner is active" || { fail "recover refuses a run whose owner is active"; cat "$work/out"; }
VRCRT_BRIDGE_URL=https://192.0.2.9:38443 python3 -I "$runner" recover --run "$run" > "$work/out" 2>&1
[ $? -eq 3 ] && grep -q runActive "$work/out" \
    && pass "recover uses the run's own claim, whatever Bridge this shell names" || { fail "recover uses the run's own claim"; cat "$work/out"; }
clients "__NOW__" "$(client 0 111 7 4 joined false authenticated)" "$(client 1 222 8 4 joined true authenticated)"
python3 -I "$runner" continue --run "$run" --claim-token "$token" > /dev/null 2>&1 &
first=$!
sleep 0.5
python3 -I "$runner" continue --run "$run" --claim-token "$token" > "$work/out" 2>&1
[ $? -eq 3 ] && grep -q notPrepared "$work/out" && pass "a second continue with the same token is refused" \
    || { fail "a second continue with the same token is refused"; cat "$work/out"; }
wait "$first"
rm -f "$root"/locks/*.json
prepare_run
python3 -I - "$run/run.json" <<'PY'
import json, sys
p = sys.argv[1]; r = json.load(open(p)); r["state"] = "running"
r["operations"].append({"name": "waitClientsReady", "inProgress": True, "result": "UNKNOWN"}); json.dump(r, open(p, "w"))
PY
python3 -I - "$root"/locks/*.json <<'PY'
import json, sys
p = sys.argv[1]; c = json.load(open(p)); c["owner"] = {"pid": 1, "start": "not this process"}; json.dump(c, open(p, "w"))
PY
python3 -I "$runner" recover --run "$run" > /dev/null 2>&1
[ "$(field "$run/run.json" reason)" = "runnerInterrupted" ] && pass "recover finalizes an interrupted run" || fail "recover finalizes an interrupted run"
rm -f "$root"/locks/*.json

prepare_run; deploy_result "$run" PASS PASS null null
printf 'SLEEP 30\n{}' > "$fake/clients.json"
cat > "$work/stubborn.sh" <<STUB
#!/usr/bin/env bash
bash -c 'trap "" TERM; echo \$\$ > "$work/stubborn.pid"; while :; do sleep 1; done' &
exec python3 -I "$work/fake.py" "\$@"
STUB
chmod +x "$work/stubborn.sh"
VRCRT_RUNNER_TEST_CLI="$work/stubborn.sh" python3 -I "$runner" continue --run "$run" --claim-token "$token" > /dev/null 2>&1 &
pid=$!
sleep 1.5
kill -INT "$pid"
wait "$pid"
code=$?
[ "$code" -eq 4 ] && [ "$(field "$run/run.json" result)" = CANCELED ] && pass "Ctrl-C ends the run as CANCELED" \
    || fail "Ctrl-C ends the run as CANCELED (exit $code, $(field "$run/run.json" result))"
if [ -f "$work/stubborn.pid" ] && kill -0 "$(cat "$work/stubborn.pid")" 2>/dev/null; then
    fail "a descendant that ignores SIGTERM is killed with its group"; kill -9 "$(cat "$work/stubborn.pid")"
else
    pass "a descendant that ignores SIGTERM is killed with its group"
fi
ls "$root"/locks/*.json > /dev/null 2>&1 && fail "after Ctrl-C the claim is released" || pass "after Ctrl-C the claim is released"
rm -f "$root"/locks/*.json

mkdir -p "$work/project/Assets/Scenes"; touch "$work/project/Assets/Scenes/Main.unity"
cat > "$work/unity" <<UNITY
#!/usr/bin/env bash
sleep 1
printf '{"schema":"vrcrt.deploy-result","schemaVersion":1,"attemptId":"20261126T000000000Z-11112222","buildId":"20261126T000001000Z-33334444","artifact":{"worldBuildTag":"%s"},"classification":{"deploy":"PASS","launch":"PASS","stopReason":null}}' "\$VRC_REMOTE_TEST_WORLD_BUILD_TAG" > "\$VRC_REMOTE_TEST_RUN_DIR/deploy-result.json"
env > "$work/unity-env.log"
UNITY
chmod +x "$work/unity"
bridge; before_clients; cursor; ready_clients RUNTAGX
VRCRT_TOKEN=test-token VRCRT_RUNNER_TEST_NO_PS=1 VRCRT_RUNNER_TEST_PREPARE=0.3 python3 -I "$runner" run --build headless \
    --project "$work/project" --scene Assets/Scenes/Main.unity --slots 0,1 --unity "$work/unity" --run-root "$root" > "$work/out" 2>&1
code=$?
headless_run="$(ls -dt "$root"/runs/*/ | head -1)"
if [ "$(field "$headless_run/run.json" results.deploy)" = PASS ]; then
    pass "a headless build is not cut short by prepare's deadline"
else
    fail "a headless build is not cut short by prepare's deadline (exit $code)"; cat "$work/out"
fi
grep -q "^VRC_REMOTE_TEST_TOKEN=test-token" "$work/unity-env.log" && ! grep -q "^VRCRT_TOKEN=" "$work/unity-env.log" \
    && pass "headless Unity gets its token and no other" || fail "headless Unity gets its token and no other"
rm -f "$root"/locks/*.json

prepare_run; ready_clients "$tag"; deploy_result "$run" PASS PASS null null
touch "$run/cancel.request"; : > "$fake/calls.log"
check "a cancel before continue starts no child" 4 result CANCELED
[ -s "$fake/calls.log" ] && fail "nothing was asked of the Bridge" || pass "nothing was asked of the Bridge"

prepare_run; ready_clients "$tag"; deploy_result "$run" PASS PASS null "\"$tag\""
detached="$(python3 -I "$runner" continue --run "$run" --claim-token "$token" --detach 2>"$work/err")"
code=$?
for _ in $(seq 100); do [ "$(field "$run/run.json" state)" = finished ] && break; sleep 0.1; done
if [ "$code" -eq 0 ] && printf '%s' "$detached" | grep -q '"pid"' && [ "$(field "$run/run.json" result)" = PASS ]; then
    pass "continue --detach returns at once and the run finishes on its own"
else
    fail "continue --detach returns at once and the run finishes on its own (exit $code)"; cat "$work/err"
fi
python3 -I "$runner" continue --run "$run" --claim-token "$token" --detach > /dev/null 2>&1
[ $? -eq 3 ] && pass "continue --detach refuses a run that is not prepared" || fail "continue --detach refuses a run that is not prepared"

# --- arguments and places
python3 -I "$runner" prepare --slots 0,0 --run-root "$root" > /dev/null 2>&1
[ $? -eq 2 ] && pass "slots must be different" || fail "slots must be different"
mkdir -p "$work/repo/.git"
python3 -I "$runner" prepare --slots 0 --run-root "$work/repo/root" > "$work/out" 2>&1
[ $? -eq 2 ] && grep -q "Git work tree" "$work/out" && pass "a run root inside Git is refused" || fail "a run root inside Git is refused"
bridge; before_clients; cursor
python3 -I "$runner" prepare --slots 0,1 --owner-pid $$ --run-root "$root" --expect-clients mode=Reload > "$work/out" 2>&1
[ $? -eq 3 ] && grep -q declarationMismatch "$work/out" && pass "a declaration that differs from the Bridge stops UNKNOWN" \
    || fail "a declaration that differs from the Bridge stops UNKNOWN"
python3 -I "$runner" continue --run "$work" --claim-token "$token" > /dev/null 2>&1
[ $? -eq 2 ] && pass "continue refuses a folder that is not a run" || fail "continue refuses a folder that is not a run"

# --- secrets: what children see
rm -f "$fake/env.log"
prepare_run
VRC_REMOTE_TEST_TOKEN=sentinel-secret-value python3 -I "$runner" prepare --slots 0,1 --owner-pid $$ --run-root "$root" > /dev/null 2>&1
if grep -q sentinel-secret-value "$fake/env.log" "$root"/runs/*/run.json 2>/dev/null; then
    fail "the Unity token variable reaches no child and no record"
else
    pass "the Unity token variable reaches no child and no record"
fi
rm -f "$root"/locks/*.json

# --- prune
old="$root/runs/20200101T000000Z-00000000"
mkdir -p "$old" && printf '{"schema":"vrcrt.run","schemaVersion":1,"state":"finished","createdAtUtc":"2020-01-01T00:00:00+00:00"}' > "$old/run.json"
ln -s "$work" "$root/runs/20200101T000000Z-11111111"
kept="$root/runs/20200101T000000Z-22222222"
mkdir -p "$kept" && printf '{"schema":"vrcrt.run","schemaVersion":1,"state":"finished","createdAtUtc":"2020-01-01T00:00:00+00:00","children":[{"pid":1,"start":"x","pgid":1}]}' > "$kept/run.json"
python3 -I "$runner" prune --older-than-days 30 --run-root "$root" > /dev/null 2>&1
[ ! -e "$old" ] && [ -L "$root/runs/20200101T000000Z-11111111" ] && [ -d "$run" ] \
    && pass "prune removes old finished runs only, never through a link" || fail "prune removes old finished runs only, never through a link"
[ -d "$kept" ] && pass "prune keeps a run that records a child never confirmed stopped" \
    || fail "prune keeps a run that records a child never confirmed stopped"
python3 -I - "$run/run.json" <<'PY'
import json, sys
p = sys.argv[1]; r = json.load(open(p)); r.pop("claimKey", None); r["state"] = "prepared"; json.dump(r, open(p, "w"))
PY
python3 -I "$runner" continue --run "$run" --claim-token 0123456789abcdef0123456789abcdef > "$work/out" 2>&1
[ $? -eq 2 ] && grep -q "no claim key" "$work/out" && pass "continue refuses a run without its claim key" \
    || { fail "continue refuses a run without its claim key"; cat "$work/out"; }

# --- deploy-collect on a Bridge with slotPreconditions.v1 (R1b)
FEATURE=1 prepare_run; ready_clients "$tag"; deploy_result "$run" PASS PASS null "\"$tag\""
check "with the feature, the deploy's launch plan and an unmoved revision pass" 0 result PASS
[ "$(field "$run/run.json" guard.operationRevision)" = 17 ] && pass "the revision after the deploy is recorded" \
    || fail "the revision after the deploy is recorded"
FEATURE=1 prepare_run; ready_clients "$tag"; deploy_result "$run" PASS PASS null "\"$tag\""
FEATURE=1 BATCH=20261126T000002000Z-55556666 bridge
check "with the feature, another deploy's launch plan is UNKNOWN" 3 reason batchChanged
FEATURE=1 prepare_run; ready_clients "$tag"; deploy_result "$run" PASS PASS null "\"$tag\""
FEATURE=1 REV=18 BRIDGE_OUT=bridge-then.json bridge
check "with the feature, a revision that moves while the clients get ready is UNKNOWN" 3 reason revisionChanged
FEATURE=1 prepare_run; ready_clients "$tag"; deploy_result "$run" PASS PASS null "\"$tag\""
FEATURE=1 BRIDGE_OUT=bridge-then.json bridge
FEATURE=1 REV=18 BRIDGE_OUT=bridge-later.json bridge
check "with the feature, a revision that moves after the clients are ready is UNKNOWN" 3 reason revisionChanged
FEATURE=1 prepare_run; ready_clients "$tag"; deploy_result "$run" PASS PASS null "\"$tag\""
FEATURE=1 BRIDGE_OUT=bridge-then.json bridge
FEATURE=1 BRIDGE_OUT=bridge-later.json bridge
python3 -I -c 'import json,sys; p=sys.argv[1]; b=json.load(open(p)); b["busy"]="close-client"; json.dump(b, open(p,"w"))' "$fake/bridge-later.json"
check "with the feature, a Bridge busy with someone else's operation is UNKNOWN" 3 reason bridgeBusy
BRIDGE_MODE=Reload FEATURE=1 prepare_run; deploy_result "$run" PASS PASS null null
BRIDGE_MODE=Reload FEATURE=1 BATCH=20261126T000002000Z-55556666 bridge
clients "__NOW__" "$(client 0 11 5 60 joined false authenticated)" "$(client 1 22 6 60 joined false authenticated)"
markers 0 11 5 "[]"; markers 1 22 6 "[]"
check "Reload with the feature keeps R1a's checks" 0 results.clientReadiness PASS

# --- leave-rejoin (R1b)
rm -f "$root"/locks/*.json
bridge; before_clients; cursor
python3 -I "$runner" prepare --slots 0,1 --procedure leave-rejoin --target-slot 1 --owner-pid $$ --run-root "$root" > "$work/out" 2>&1
[ $? -ne 2 ] && ! grep -q -- --unvalidated "$work/out" && pass "leave-rejoin, validated on Windows, needs no --unvalidated" \
    || { fail "leave-rejoin, validated on Windows, needs no --unvalidated"; cat "$work/out"; }
python3 -I "$runner" prepare --slots 0,1 --procedure leave-rejoin --target-slot 2 --unvalidated --owner-pid $$ \
    --run-root "$root" > "$work/out" 2>&1
[ $? -eq 2 ] && pass "a target slot outside --slots is refused" || { fail "a target slot outside --slots is refused"; cat "$work/out"; }
python3 -I "$runner" prepare --slots 0,1 --target-slot 1 --owner-pid $$ --run-root "$root" > "$work/out" 2>&1
[ $? -eq 2 ] && pass "--target-slot without leave-rejoin is refused" || { fail "--target-slot without leave-rejoin is refused"; cat "$work/out"; }
python3 -I "$runner" prepare --slots 0,1 --procedure leave-rejoin --target-slot 1 --unvalidated --remaining-marker '(' \
    --owner-pid $$ --run-root "$root" > "$work/out" 2>&1
[ $? -eq 2 ] && pass "a --remaining-marker that does not compile is refused" || { fail "a --remaining-marker that does not compile is refused"; cat "$work/out"; }
: > "$fake/calls.log"
python3 -I "$runner" prepare --slots 0,1 --procedure leave-rejoin --target-slot 1 --unvalidated --owner-pid $$ \
    --run-root "$root" > "$work/out" 2>&1
code=$?
if [ "$code" -eq 3 ] && grep -q slotPreconditionsUnsupported "$work/out" && ! grep -q -- '-client' "$fake/calls.log"; then
    pass "a Bridge without the feature gets no close or launch, and the run is UNKNOWN"
else
    fail "a Bridge without the feature gets no close or launch (exit $code)"; cat "$work/out"
fi
lr_run="$(ls -dt "$root"/runs/*/ | head -1)"
[ "$(field "$lr_run/run.json" results.leaveRejoin)" = UNKNOWN ] && pass "leave-rejoin not started is UNKNOWN" \
    || fail "leave-rejoin not started is UNKNOWN"

owner_marker='{"seq":60,"text":"2026.11.26 00:00:00 Debug - [VRCRT] PRT001 ev=OWNER_CHANGED","truncated":%s,"withheld":false}'
# lr_prepare [prepare args...]: a 0.12 Bridge with both clients ready and tagged, a passing deploy
# result, and what the Bridge does and answers for the close and the launch of slot 1, staged.
lr_prepare() {
    FEATURE=1 prepare_run --procedure leave-rejoin --target-slot 1 --unvalidated "$@" || return $?
    lr_stage
}
# lr_stage: both clients ready and tagged, a passing deploy result, and the close and launch of slot 1 staged.
lr_stage() {
    ready_clients "$tag"; deploy_result "$run" PASS PASS null "\"$tag\""
    FEATURE=1 REV=18 BRIDGE_OUT=bridge-after-close.json bridge
    CLIENTS_OUT=clients-after-close.json clients "__NOW__" "$(client 0 111 7 4 joined false authenticated)" \
        "$(RUNNING=false client 1 222 8 4 joined false authenticated)"
    MARKERS_OUT=markers-0-after-close.json markers 0 111 7 \
        "[$(tagged "$tag" | sed 's/^\[//; s/\]$//'),$(printf "$owner_marker" "${CUT:-false}")]"
    printf '{"slot":1,"processId":101,"closed":1,"notConfirmed":0,"recordUpdated":true,"problems":[],"operationRevision":%s,"bridgeInstanceId":"%s","operationId":"__OPID__","clientGeneration":222}' \
        "${CLOSE_REV:-18}" "$instance_id" > "$fake/close.json"
    FEATURE=1 REV=19 BRIDGE_OUT=bridge-after-launch.json bridge
    CLIENTS_OUT=clients-after-launch.json clients "__NOW__" "$(client 0 111 7 4 joined false authenticated)" \
        "$(client 1 333 9 4 joined false authenticated)"
    MARKERS_OUT=markers-1-after-launch.json markers 1 333 9 "$(tagged "$tag")"
    printf '{"slot":1,"profile":1,"processId":201,"clientGeneration":333,"operationRevision":19,"bridgeInstanceId":"%s","operationId":"__OPID__"}' \
        "$instance_id" > "$fake/launch.json"
    : > "$fake/calls.log"
}
calls() { grep -c "^$1-client" "$fake/calls.log"; }
cancel_after_close() {
    ( for _ in $(seq 400); do grep -q '^close-client' "$fake/calls.log" 2>/dev/null && break; sleep 0.05; done
      touch "$run/cancel.request" ) &
}

lr_prepare --remaining-marker 'ev=OWNER_CHANGED'
check "leave-rejoin passes when the slot leaves and comes back as declared" 0 results.leaveRejoin PASS
[ "$(field "$run/run.json" result)" = PASS ] && [ "$(field "$run/run.json" leaveRejoin.newGeneration)" = 333 ] \
    && pass "the run passes with the new generation recorded" || fail "the run passes with the new generation recorded"
run_id="$(basename "$run")"
printf 'close-client 1 --expect @%s/expect-close.json --operation-id %s-close --json\nlaunch-client 1 --expect @%s/expect-launch.json --operation-id %s-launch --json\n' \
    "$run" "$run_id" "$run" "$run_id" > "$work/want-calls"
if grep -E '^(close|launch)-client' "$fake/calls.log" | cmp -s - "$work/want-calls"; then
    pass "exactly one close, then one launch, each with its expect file and operation ID"
else
    fail "exactly one close, then one launch, each with its expect file and operation ID"; grep -- '-client' "$fake/calls.log"
fi
python3 -I - "$fake/expect-close.seen" "$fake/expect-launch.seen" "$instance_id" <<'PY' && pass "the expect files state the Bridge and generations read before each request" || fail "the expect files state the Bridge and generations read before each request"
import json, sys
base = {"bridgeInstanceId": sys.argv[3], "hostGeneration": 2, "activeConfigEtag": "e1", "savedConfigEtag": "e1",
        "buildId": "20261126T000001000Z-33334444", "clientGeneration": 222}
close, launch = json.load(open(sys.argv[1])), json.load(open(sys.argv[2]))
sys.exit(0 if close == dict(base, operationRevision=17) and launch == dict(base, operationRevision=18) else 1)
PY
cmp -s "$fake/expect-close.seen" "$run/expect-close.json" && [ "$(stat -f %Lp "$run/expect-close.json")" = "600" ] \
    && pass "the expect file is the run's own, owner-only" || fail "the expect file is the run's own, owner-only"
ls "$run"/evidence/leave-rejoin/before-close/slot-1/g222-e8/vrchat-slot-1-*.log > /dev/null 2>&1 \
    && [ -f "$run/evidence/leave-rejoin/after-close/slot-0/g111-e7/markers.json" ] \
    && [ -f "$run/evidence/leave-rejoin/after-close/slot-1/g222-e8/markers.json" ] \
    && ls "$run"/evidence/leave-rejoin/after-rejoin/slot-1/g333-e9/vrchat-slot-1-*.log > /dev/null 2>&1 \
    && [ -f "$run/evidence/slot-1/g222-e8/markers.json" ] \
    && pass "evidence before the close, after it and of the new generation is kept apart" \
    || fail "evidence before the close, after it and of the new generation is kept apart"

# The bytes carry a tag the first join did not show: another tag in the new generation still fails,
# no tag there leaves leave-rejoin as it was.
lr_prepare; markers 0 111 7 "[]"; markers 1 222 8 "[]"
MARKERS_OUT=markers-1-after-launch.json markers 1 333 9 "$(tagged OTHERTAG1)"
check "another tag in the rejoined generation fails, even when the first join showed none" 1 results.leaveRejoin FAIL
lr_prepare; markers 0 111 7 "[]"; markers 1 222 8 "[]"
MARKERS_OUT=markers-1-after-launch.json markers 1 333 9 "[]"
check "no tag in the rejoined generation of a world that showed none is not held against it" 0 results.leaveRejoin PASS
CUT=true lr_prepare --remaining-marker 'ev=OWNER_CHANGED'
check "a cut marker line does not satisfy --remaining-marker" 1 reason remainingMarkerMissing
[ "$(field "$run/run.json" results.leaveRejoin)" = FAIL ] && [ "$(calls launch)" = 0 ] \
    && [ "$(field "$run/run.json" leaveRejoin.clientMayBeLeftClosed)" = True ] \
    && pass "a missing marker fails leave-rejoin and records the client left closed" \
    || fail "a missing marker fails leave-rejoin and records the client left closed"

lr_prepare
rm -f "$fake"/*-after-close.json
printf 'EXIT 1\n{"error":"preconditionFailed","mismatched":["operationRevision"],"current":{"operationRevision":18,"clientGeneration":null},"operationId":"__OPID__"}' > "$fake/close.json"
check "a 412 for the close is UNKNOWN" 3 reason closePreconditionFailed
[ "$(calls close)" = 1 ] && [ "$(calls launch)" = 0 ] && [ "$(field "$run/run.json" leaveRejoin.close.answer.mismatched)" = "['operationRevision']" ] \
    && [ "$(field "$run/run.json" leaveRejoin.close.reread.bridge.operationRevision)" = 17 ] \
    && pass "after a 412 nothing is sent again and the Bridge is read again into run.json" \
    || fail "after a 412 nothing is sent again and the Bridge is read again into run.json"

CLOSE_REV=19 lr_prepare
check "a close answer whose revision is not expect + 1 is UNKNOWN" 3 reason closeAnswerUnexpected
[ "$(calls close)" = 1 ] && [ "$(calls launch)" = 0 ] && pass "an unexpected close answer launches nothing" \
    || fail "an unexpected close answer launches nothing"

lr_prepare
CLIENTS_OUT=clients-after-close.json clients "__NOW__" "$(client 0 111 7 5 joined false authenticated)" \
    "$(RUNNING=false client 1 222 8 4 joined false authenticated)"
check "a slot that stays but joins again during the wait is UNKNOWN" 3 reason unexpectedChange
[ "$(calls launch)" = 0 ] && [ "$(field "$run/run.json" leaveRejoin.reread.clients.processListReadable)" = True ] \
    && pass "a change while waiting launches nothing and the lists are read again into run.json" \
    || fail "a change while waiting launches nothing and the lists are read again into run.json"

lr_prepare
printf 'EXIT 3\n' > "$fake/launch.json"
check "a launch whose outcome is unknown is UNKNOWN" 3 reason launchUnavailable
[ "$(calls close)" = 1 ] && [ "$(calls launch)" = 1 ] && grep -q "may be left closed" "$run/run.json" \
    && pass "the launch is not sent again and the run says the client may be left closed" \
    || fail "the launch is not sent again and the run says the client may be left closed"

lr_prepare
rm -f "$fake"/clients-after-close.json
cancel_after_close
VRCRT_RUNNER_TEST_LEAVE=10 check "a cancel while the slot leaves is CANCELED" 4 results.leaveRejoin CANCELED
[ "$(calls launch)" = 0 ] && pass "a cancel launches nothing" || fail "a cancel launches nothing"

lr_prepare
printf 'SLEEP 30\n%s' "$(cat "$fake/close.json")" > "$fake/close.json"
cancel_after_close
start=$(python3 -I -c 'import time; print(time.monotonic())')
check "a cancel during the close is CANCELED" 4 reason closeCanceled
elapsed=$(python3 -I -c "import time; print(int(time.monotonic() - $start))")
[ "$(calls close)" = 1 ] && [ "$(calls launch)" = 0 ] && [ "$(field "$run/run.json" leaveRejoin.close.outcome)" = unknown ] \
    && [ "$elapsed" -lt 15 ] && pass "the close's outcome is recorded as unknown and nothing is sent again" \
    || fail "the close's outcome is recorded as unknown and nothing is sent again (${elapsed} s)"

# --- manual-steps (R3)
export VRCRT_RUNNER_TEST_ANSWER_WAIT=6
export VRCRT_RUNNER_TEST_MANUAL_RECHECK=0.2
export VRCRT_RUNNER_TEST_OBSERVATION_GRACE=0.6
checklist="$work/checklist.json"
printf '{"schema":"vrcrt.checklist","schemaVersion":1,"title":"Hand order","items":[{"id":"a","text":"first"},{"id":"b","text":"second \\u001b[31m"},{"id":"c","text":"third"}]}' > "$checklist"
ms_prepare() {
    FEATURE=1 prepare_run --procedure manual-steps --checklist "$checklist" --unvalidated "$@" || return $?
    ready_clients "$tag"; deploy_result "$run" PASS PASS null "\"$tag\""
    : > "$fake/calls.log"
}
# waiting_json: one `waiting --json` of $run (exit status of waiting)
waiting_json() { python3 -I "$runner" waiting --run "$run" --json 2>/dev/null; }
jget() { python3 -I -c 'import json,sys; d=json.loads(sys.argv[1]); print(d.get(sys.argv[2]))' "$1" "$2"; }
# answer_items <answer>...: in the background, answers each item in turn once the run waits for it.
answer_items() {
    ( last=""
      for a in "$@"; do
          out=""
          for _ in $(seq 600); do
              out="$(waiting_json)" && [ "$(jget "$out" waitId)" != "$last" ] && break
              out=""; sleep 0.05
          done
          [ -n "$out" ] || exit 0
          last="$(jget "$out" waitId)"
          python3 -I "$runner" answer --run "$run" --wait-id "$last" --item "$(jget "$out" itemId)" "$a" --memo "memo $a" \
              > /dev/null 2>&1
      done ) &
}
ms_refused() {
    local name="$1"; shift
    rm -f "$root"/locks/*.json; bridge; before_clients; cursor
    python3 -I "$runner" prepare --slots 0,1 --owner-pid $$ --run-root "$root" "$@" > "$work/out" 2>&1
    [ $? -eq 2 ] && pass "$name" || { fail "$name"; cat "$work/out"; }
}
rm -f "$root"/locks/*.json; bridge; before_clients; cursor
python3 -I "$runner" prepare --slots 0,1 --procedure manual-steps --checklist "$checklist" --owner-pid $$ --run-root "$root" > "$work/out" 2>&1
[ $? -ne 2 ] && ! grep -q -- --unvalidated "$work/out" && pass "manual-steps, validated on Windows, needs no --unvalidated" \
    || { fail "manual-steps, validated on Windows, needs no --unvalidated"; cat "$work/out"; }
ms_refused "manual-steps needs --checklist" --procedure manual-steps --unvalidated
ms_refused "--checklist belongs to manual-steps" --checklist "$checklist"
ms_refused "--run-minutes below 30 is refused" --procedure manual-steps --checklist "$checklist" --unvalidated --run-minutes 20
ms_refused "--answer-wait below 60 is refused" --procedure manual-steps --checklist "$checklist" --unvalidated --answer-wait 5
ms_refused "--leave-rejoin-after must name an item" --procedure manual-steps --checklist "$checklist" --unvalidated \
    --leave-rejoin-after zz --target-slot 1
ms_refused "--leave-rejoin-after needs a target slot in --slots" --procedure manual-steps --checklist "$checklist" --unvalidated \
    --leave-rejoin-after b --target-slot 2
ms_refused "--remaining-marker without --leave-rejoin-after is refused" --procedure manual-steps --checklist "$checklist" \
    --unvalidated --remaining-marker x
bad="$work/bad-checklist.json"
for body in '{"schema":"vrcrt.checklist","schemaVersion":1,"title":"t","title":"u","items":[{"id":"a","text":"x"}]}' \
            '{"schema":"vrcrt.checklist","schemaVersion":1,"title":"t","items":[{"id":"A","text":"x"}]}' \
            '{"schema":"vrcrt.checklist","schemaVersion":1,"title":"t","items":[{"id":"a","text":"x","extra":1}]}' \
            '{"schema":"vrcrt.checklist","schemaVersion":1,"title":"t","items":[{"id":"a","text":"\ud800"}]}' \
            '{"schema":"vrcrt.checklist","schemaVersion":1,"title":"t","items":[]}'; do
    printf '%s' "$body" > "$bad"
    ms_refused "a checklist that is not the contract's shape is refused ($(printf '%s' "$body" | cut -c50-80))" \
        --procedure manual-steps --checklist "$bad" --unvalidated
done
python3 -I -c 'import json,sys; json.dump({"schema":"vrcrt.checklist","schemaVersion":1,"title":"t","items":[{"id":"i%d" % n,"text":"x"} for n in range(21)]}, open(sys.argv[1],"w"))' "$bad"
ms_refused "a checklist of 21 items is refused" --procedure manual-steps --checklist "$bad" --unvalidated
ln -sf "$checklist" "$work/link-checklist.json"
ms_refused "a checklist that is a link is refused" --procedure manual-steps --checklist "$work/link-checklist.json" --unvalidated

ms_prepare
[ "$(stat -f %Lp "$run/checklist.json")" = 600 ] && cmp -s "$checklist" "$run/checklist.json" \
    && [ "$(field "$run/run.json" manualSteps.items)" != None ] && pass "prepare keeps an owner-only copy of the checklist" \
    || fail "prepare keeps an owner-only copy of the checklist"
python3 -I - "$run/run.json" "$root/locks" <<'PY' && pass "a manual-steps run and its claim have one 90-minute deadline" || fail "a manual-steps run and its claim have one 90-minute deadline"
import glob, json, sys
from datetime import datetime
r = json.load(open(sys.argv[1])); c = json.load(open(glob.glob(sys.argv[2] + "/*.json")[0]))
span = datetime.fromisoformat(r["deadlineUtc"]) - datetime.fromisoformat(r["createdAtUtc"])
sys.exit(0 if abs(span.total_seconds() - 5400) < 5 and c["deadlineUtc"] == r["deadlineUtc"] else 1)
PY
answer_items ok ok ok
check "manual-steps passes when every item is answered ok" 0 results.manualSteps PASS
[ "$(field "$run/run.json" result)" = PASS ] && [ "$(field "$run/run.json" testVerdict)" = None ] \
    && [ "$(field "$run/run.json" results.testVerdict)" = notJudged ] && pass "the run passes and the test is still not judged" \
    || fail "the run passes and the test is still not judged"
python3 -I - "$run" <<'PY' && pass "each answer is recorded with its range and markers, and the end evidence is kept" || fail "each answer is recorded with its range and markers, and the end evidence is kept"
import json, os, sys
d = sys.argv[1]; r = json.load(open(os.path.join(d, "run.json")))
items = r["manualSteps"]["items"]
ok = [i["state"] for i in items] == ["answered"] * 3 and [i["answer"] for i in items] == ["ok"] * 3
ok = ok and all(i["memo"] == "memo ok" and i["by"] == "cli" and i["seqRange"]["0"] == {"from": 50, "to": 50} for i in items)
ok = ok and all(os.path.isfile(os.path.join(d, i["evidence"], "slot-1", "g222-e8", "markers.json")) for i in items)
ok = ok and os.path.isdir(os.path.join(d, "evidence", "manual-steps", "end", "slot-0", "g111-e7"))
w = json.load(open(os.path.join(d, "waiting.json")))
sys.exit(0 if ok and w["waiting"] is False and r["results"]["evidence"] == "PASS" else 1)
PY
python3 -I "$runner" answer --run "$run" --wait-id "$(printf 'f%.0s' $(seq 32))" --item a ok > /dev/null 2>&1
[ $? -eq 1 ] && pass "an answer after the run finished is refused" || fail "an answer after the run finished is refused"

ms_prepare; answer_items ng skip ok
check "an ng among the answers fails the run" 1 results.manualSteps FAIL
[ "$(field "$run/run.json" reason)" = humanNg ] && [ "$(field "$run/run.json" manualSteps.items)" != None ] \
    && python3 -I -c 'import json,sys; r=json.load(open(sys.argv[1])); sys.exit(0 if [i["answer"] for i in r["manualSteps"]["items"]]==["ng","skip","ok"] else 1)' "$run/run.json" \
    && pass "after an ng every item is still asked" || fail "after an ng every item is still asked"
ms_prepare; answer_items ok skip ok
check "a skipped item leaves the human result UNKNOWN" 3 reason humanNotAllOk

ms_prepare
VRCRT_RUNNER_TEST_ANSWER_WAIT=1 check "no answer in time is UNKNOWN" 3 reason answerTimeout
python3 -I -c 'import json,sys; r=json.load(open(sys.argv[1])); s=[i["state"] for i in r["manualSteps"]["items"]]
sys.exit(0 if s==["unanswered","notAsked","notAsked"] and r["results"]["manualSteps"]=="UNKNOWN" else 1)' "$run/run.json" \
    && [ "$(field "$run/waiting.json" waiting)" = False ] && pass "the late item is unanswered, the rest not asked, the wait closed" \
    || fail "the late item is unanswered, the rest not asked, the wait closed"

ms_prepare
( for _ in $(seq 400); do waiting_json > /dev/null && break; sleep 0.05; done; touch "$run/cancel.request" ) &
start=$(python3 -I -c 'import time; print(time.monotonic())')
check "a cancel while waiting for an answer is CANCELED" 4 results.manualSteps CANCELED
elapsed=$(python3 -I -c "import time; print(time.monotonic() - $start < 4)")
[ "$elapsed" = True ] && [ "$(field "$run/run.json" manualSteps.items)" != None ] \
    && python3 -I -c 'import json,sys; r=json.load(open(sys.argv[1])); sys.exit(0 if r["manualSteps"]["items"][0]["state"]=="interrupted" else 1)' "$run/run.json" \
    && pass "the cancel takes effect at once and the item is interrupted" || fail "the cancel takes effect at once and the item is interrupted"

ms_prepare
( for _ in $(seq 400); do waiting_json > /dev/null && break; sleep 0.05; done
  clients "__NOW__" "$(client 0 111 7 4 joined false authenticated)" "$(RUNNING=false client 1 222 8 4 joined false authenticated)" ) &
check "a client that leaves while the run waits is UNKNOWN" 3 reason clientChanged
ms_prepare
( for _ in $(seq 400); do waiting_json > /dev/null && break; sleep 0.05; done
  FEATURE=1 REV=18 bridge ) &
check "a revision that moves while the run waits is UNKNOWN" 3 reason clientChanged
# statuses: how many times the runner has read /clients so far
statuses() { grep -c '^status' "$fake/calls.log"; }
ms_prepare
( for _ in $(seq 400); do waiting_json > /dev/null && break; sleep 0.05; done
  clients "2026-01-01T00:00:00+00:00" "$(client 0 111 7 4 joined false authenticated)" "$(client 1 222 8 4 joined false authenticated)"
  seen=$(statuses); for _ in $(seq 400); do [ "$(statuses)" -gt "$seen" ] && break; sleep 0.05; done
  : > "$work/stale-seen"
  ready_clients "$tag"
  seen=$(statuses); for _ in $(seq 400); do [ "$(statuses)" -gt "$seen" ] && break; sleep 0.05; done
  answer_items ok ok ok; wait ) &
rm -f "$work/stale-seen"
VRCRT_RUNNER_TEST_OBSERVATION_GRACE=3 check "a short gap in the observation does not stop the wait" 0 result PASS
[ -f "$work/stale-seen" ] && pass "(the runner did read the stale list while it waited)" \
    || fail "(the runner did read the stale list while it waited)"
ms_prepare
( for _ in $(seq 400); do waiting_json > /dev/null && break; sleep 0.05; done
  clients "2026-01-01T00:00:00+00:00" "$(client 0 111 7 4 joined false authenticated)" "$(client 1 222 8 4 joined false authenticated)" ) &
check "an observation that stays stale while waiting is UNKNOWN" 3 reason observationIncomplete

# answer's refusals, and answers that are not the runner's
ms_prepare
python3 -I "$runner" continue --run "$run" --claim-token "$token" > "$work/out" 2>&1 &
continue_pid=$!
out=""; for _ in $(seq 400); do out="$(waiting_json)" && break; sleep 0.05; done
wait_id="$(jget "$out" waitId)"
[ "$(jget "$out" title)" = "Hand order" ] && [ "$(jget "$out" index)" = 1 ] && pass "waiting shows the item and the run's title" \
    || fail "waiting shows the item and the run's title: $out"
python3 -I "$runner" answer --run "$run" --wait-id "$(printf '0%.0s' $(seq 32))" --item a ok > /dev/null 2>&1
code1=$?
python3 -I "$runner" answer --run "$run" --wait-id "$wait_id" --item b ok > /dev/null 2>&1
code2=$?
[ "$code1" -eq 1 ] && [ "$code2" -eq 1 ] && [ -z "$(ls "$run"/answer-* 2>/dev/null)" ] \
    && pass "an answer to another wait or item is refused and nothing is written" || fail "an answer to another wait or item is refused ($code1 $code2)"
kill -9 "$continue_pid"; wait "$continue_pid" 2>/dev/null
python3 -I "$runner" answer --run "$run" --wait-id "$wait_id" --item a ok > /dev/null 2>&1
[ $? -eq 1 ] && [ -z "$(ls "$run"/answer-* 2>/dev/null)" ] && pass "an answer after the runner died is refused" \
    || fail "an answer after the runner died is refused"
python3 -I "$runner" recover --run "$run" > /dev/null 2>&1
python3 -I -c 'import json,sys; r=json.load(open(sys.argv[1])); sys.exit(0 if r["reason"]=="runnerInterrupted" and r["manualSteps"]["items"][0]["state"]=="interrupted" and r["results"]["manualSteps"]=="UNKNOWN" else 1)' "$run/run.json" \
    && [ "$(field "$run/waiting.json" waiting)" = False ] && pass "recover closes the wait and marks the item interrupted" \
    || fail "recover closes the wait and marks the item interrupted"

ms_prepare
python3 -I "$runner" continue --run "$run" --claim-token "$token" > "$work/out" 2>&1 &
continue_pid=$!
out=""; for _ in $(seq 400); do out="$(waiting_json)" && break; sleep 0.05; done
wait_id="$(jget "$out" waitId)"
python3 -I "$runner" answer --run "$run" --wait-id "$wait_id" --item a ok > /dev/null 2>&1; code1=$?
python3 -I "$runner" answer --run "$run" --wait-id "$wait_id" --item a ng > /dev/null 2>&1; code2=$?
touch "$run/cancel.request"; wait "$continue_pid"
[ "$code1" -eq 0 ] && [ "$code2" -eq 1 ] && [ "$(field "$run/answer-$wait_id.json" answer)" = ok ] \
    && pass "a second answer to the same wait is refused and the first stays" || fail "a second answer to the same wait is refused ($code1 $code2)"

ms_prepare
( for _ in $(seq 400); do out="$(waiting_json)" && break; sleep 0.05; done
  printf '{"schema":"vrcrt.answer"}' > "$run/answer-$(jget "$out" waitId).json" ) &
check "an answer file that is not the contract's shape is UNKNOWN" 3 reason invalidAnswer
ms_prepare
( for _ in $(seq 400); do out="$(waiting_json)" && break; sleep 0.05; done
  mkfifo "$run/answer-$(jget "$out" waitId).json" ) &
start=$(python3 -I -c 'import time; print(time.monotonic())')
check "a FIFO in place of the answer is UNKNOWN without hanging" 3 reason invalidAnswer
ms_prepare
( for _ in $(seq 400); do out="$(waiting_json)" && break; sleep 0.05; done
  printf '{}' > "$work/elsewhere.json"; ln -s "$work/elsewhere.json" "$run/answer-$(jget "$out" waitId).json" ) &
check "a link in place of the answer is UNKNOWN" 3 reason invalidAnswer

ms_prepare
( for _ in $(seq 400); do out="$(waiting_json)" && break; sleep 0.05; done
  python3 -I "$runner" waiting --run "$run" --json > "$work/waiting-b.json" 2>&1
  python3 -I "$runner" answer --run "$run" --wait-id "$(jget "$out" waitId)" --item a ok > /dev/null 2>&1
  for _ in $(seq 400); do out2="$(waiting_json)" && [ "$(jget "$out2" itemId)" = b ] && break; sleep 0.05; done
  printf '%s' "$out2" > "$work/waiting-b.json"; touch "$run/cancel.request" ) &
check "(a run canceled at its second item)" 4 results.manualSteps CANCELED
grep -q '\\\\u001b\[31m' "$work/waiting-b.json" && ! grep -q "$(printf '\033')" "$work/waiting-b.json" \
    && pass "waiting shows an escape character in an item's text as \\u001b" || { fail "waiting shows an escape character as \\u001b"; cat "$work/waiting-b.json"; }

# manual-steps with a leave and rejoin after an item
ms_lr_prepare() {
    FEATURE=1 prepare_run --procedure manual-steps --checklist "$checklist" --unvalidated --target-slot 1 "$@" || return $?
    lr_stage
}
ms_lr_prepare --leave-rejoin-after b
answer_items ok ok ok
check "manual-steps with a leave and rejoin after an item passes" 0 result PASS
[ "$(field "$run/run.json" results.leaveRejoin)" = PASS ] && [ "$(calls close)" = 1 ] && [ "$(calls launch)" = 1 ] \
    && [ -d "$run/evidence/manual-steps/leave-rejoin/after-rejoin/slot-1/g333-e9" ] \
    && [ -d "$run/evidence/manual-steps/end/slot-1/g333-e9" ] \
    && python3 -I -c 'import json,sys; r=json.load(open(sys.argv[1])); i=r["manualSteps"]["items"]; sys.exit(0 if i[2]["state"]=="answered" and r["manualSteps"]["fixedAfterRejoin"]["1"]["clientGeneration"]==333 else 1)' "$run/run.json" \
    && pass "one close and one launch after the item, the next item asked with the new generation" \
    || fail "one close and one launch after the item, the next item asked with the new generation"
ms_lr_prepare --leave-rejoin-after c
answer_items ok ok ok
check "a leave and rejoin after the last item passes" 0 result PASS
[ -d "$run/evidence/manual-steps/end/slot-1/g333-e9" ] && pass "the end evidence is of the rejoined generation" \
    || fail "the end evidence is of the rejoined generation"
ms_lr_prepare --leave-rejoin-after a
rm -f "$fake"/*-after-close.json
printf 'EXIT 1\n{"error":"preconditionFailed","mismatched":["operationRevision"],"current":{"operationRevision":18,"clientGeneration":null},"operationId":"__OPID__"}' > "$fake/close.json"
answer_items ok ok ok
check "a 412 for the close after an item is UNKNOWN" 3 reason closePreconditionFailed
[ "$(calls launch)" = 0 ] && [ "$(field "$run/run.json" manualSteps.leaveRejoin.clientMayBeLeftClosed)" = False ] \
    && python3 -I -c 'import json,sys; r=json.load(open(sys.argv[1])); s=[i["state"] for i in r["manualSteps"]["items"]]; sys.exit(0 if s==["answered","notAsked","notAsked"] else 1)' "$run/run.json" \
    && pass "after the 412 nothing is launched and the later items are not asked" \
    || fail "after the 412 nothing is launched and the later items are not asked"
ms_lr_prepare --leave-rejoin-after a
printf 'EXIT 3\n' > "$fake/launch.json"
answer_items ok ok ok
check "a launch with no answer after an item is UNKNOWN" 3 reason launchUnavailable
[ "$(calls launch)" = 1 ] && [ "$(field "$run/run.json" manualSteps.leaveRejoin.clientMayBeLeftClosed)" = True ] \
    && pass "the launch is not sent again and the run says the client may be left closed" \
    || fail "the launch is not sent again and the run says the client may be left closed (manual-steps)"
rm -f "$root"/locks/*.json; bridge; before_clients; cursor; : > "$fake/calls.log"
python3 -I "$runner" prepare --slots 0,1 --procedure manual-steps --checklist "$checklist" --unvalidated --leave-rejoin-after b \
    --target-slot 1 --owner-pid $$ --run-root "$root" > "$work/out" 2>&1
[ $? -eq 3 ] && grep -q slotPreconditionsUnsupported "$work/out" && pass "a leave and rejoin on a Bridge without the feature is refused before the deploy" \
    || { fail "a leave and rejoin on a Bridge without the feature is refused before the deploy"; cat "$work/out"; }

if [ "$failures" -eq 0 ]; then
    echo "all checks passed"
else
    echo "$failures check(s) failed"
    exit 1
fi
