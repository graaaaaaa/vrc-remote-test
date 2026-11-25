#!/usr/bin/env bash
# Checks scripts/vrcrt-run.py's old interface with a fake Unity executable (it writes the log
# lines a real one writes, and optionally a deploy result) and a stand-in for vrcrt.sh.
# Run from anywhere: bash scripts/test-vrcrt-run.sh
set -u

here="$(CDPATH= cd -P -- "$(dirname -- "$0")" && pwd)"
work="$(mktemp -d "${TMPDIR:-/tmp}/vrcrt-run-test.XXXXXX")"
trap 'rm -rf "$work"' EXIT
export VRCRT_FIXTURE_DIR="$work/fixture"
export VRCRT_RUNNER_TEST_CLI="$work/fake-cli.sh"
export VRCRT_BRIDGE_URL="https://192.0.2.1:38443"
export VRCRT_CERT_SHA256="$(printf 'a%.0s' $(seq 64))"
export VRCRT_TOKEN="test-token"
mkdir -p "$VRCRT_FIXTURE_DIR" "$work/project/Assets/Scenes"
touch "$work/project/Assets/Scenes/Main.unity"
BUILD=20261126T000001000Z-33334444

failures=0
pass() { printf 'ok   %s\n' "$1"; }
fail() { printf 'FAIL %s\n' "$1"; failures=$((failures + 1)); }

# The CLI stand-in: logs each call; status lists one client without --watch-worlds.
cat > "$work/fake-cli.sh" <<EOF
#!/usr/bin/env bash
printf '%s\n' "\$*" >> "$work/calls.log"
case "\$1" in
    cursor) printf '{"schemaVersion":1,"slots":{}}' ;;
    result) printf '{"buildId":"%s","status":"deployed"}' "\$2" ;;
    status) printf '{"clients":[{"slot":0,"running":true,"watchWorlds":false}]}' ;;
    export-log) printf '%s/vrchat-slot-0-generation-1-epoch-1-x.log\n' "\$3" ;;
    wait-marker) printf '0\t5\tmatched\n' ;;
esac
EOF
# fake Unity: MODE=new writes a deploy result and its log line; MODE=old only the build ID line.
cat > "$work/unity" <<EOF
#!/usr/bin/env bash
while [ "\$#" -gt 0 ]; do [ "\$1" = "-logFile" ] && log="\$2"; shift; done
printf '[VRC Remote Test] Upload complete. Build ID: %s\n' "$BUILD" > "\$log"
if [ "\${MODE:-new}" = new ]; then
    mkdir -p "$work/Application Support"
    printf '{"schema":"vrcrt.deploy-result","schemaVersion":1,"buildId":"%s"}' "$BUILD" > "$work/Application Support/result.json"
    printf '[VRC Remote Test] Deploy result: %s\n' "$work/Application Support/result.json" >> "\$log"
fi
exit "\${UNITY_EXIT:-0}"
EOF
chmod +x "$work/fake-cli.sh" "$work/unity"

# run <name> <expected exit> <text the output must contain> <output dir> [extra args...]
run() {
    local name="$1" code="$2" text="$3" out="$4" status
    shift 4
    python3 -I "$here/vrcrt-run.py" --project "$work/project" --scene Assets/Scenes/Main.unity \
        --output-dir "$out" --unity "$work/unity" "$@" > "$work/out" 2>&1
    status=$?
    if [ "$status" -ne "$code" ] || ! grep -qF -- "$text" "$work/out"; then
        fail "$name: exit $status (want $code)"; sed 's/^/     /' "$work/out"
    else
        pass "$name"
    fi
}

run "a deploy succeeds and names its build" 0 "Deployed build $BUILD" "$work/out1"
grep -q "read from the log" "$work/out" && fail "the build ID comes from the deploy result, even under a folder with a space" \
    || pass "the build ID comes from the deploy result, even under a folder with a space"
for file in unity-editor.log unity-console.log build-result.json clients.json; do
    [ -f "$work/out1/$file" ] && pass "keeps $file" || fail "keeps $file"
done
grep -q "not using --watch-worlds" "$work/out" && pass "a client without --watch-worlds is only a warning" \
    || fail "a client without --watch-worlds is only a warning"
[ "$(stat -f %Lp "$work/out1")" = "700" ] && pass "the output folder is owner-only" || fail "the output folder is owner-only"
grep -q "^cursor" "$work/calls.log" && fail "no cursor without --expect-marker" || pass "no cursor without --expect-marker"

run "--expect-marker waits for the marker" 0 "Deployed build" "$work/out2" --expect-marker 'PRT' --marker-slot 0 --marker-timeout 5
[ -f "$work/out2/matched-markers.txt" ] && [ -f "$work/out2/before.cursor.json" ] && pass "keeps the cursor and the match" \
    || fail "keeps the cursor and the match"
grep -q "wait-marker 0 PRT 5 --after @" "$work/calls.log" && pass "passes --marker-slot and --marker-timeout" \
    || fail "passes --marker-slot and --marker-timeout"

MODE=old run "an older package falls back to the log line" 0 "read from the log" "$work/out3"
UNITY_EXIT=1 run "Unity failing is exit 1" 1 "Unity exited 1" "$work/out4"
run "an existing output folder is refused" 1 "File exists" "$work/out1"
mkdir -p "$work/repo/.git"
run "an output folder inside Git is refused" 2 "Git work tree" "$work/repo/evidence"

if [ "$failures" -eq 0 ]; then
    echo "all checks passed"
else
    echo "$failures check(s) failed"
    exit 1
fi
