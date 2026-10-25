#!/usr/bin/env python3
"""Build a saved Unity scene, deploy it, and collect Bridge evidence without UI clicks."""

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys


def run_cli(cli, environment, *args):
    result = subprocess.run([str(cli), *args], env=environment, text=True, capture_output=True)
    if result.returncode:
        raise RuntimeError("vrcrt.sh %s failed: %s" % (" ".join(args), result.stderr.strip()))
    return result.stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=Path, required=True, help="Unity project directory (close its interactive Editor first)")
    parser.add_argument("--scene", required=True, help="saved scene path relative to the project, under Assets/")
    parser.add_argument("--output-dir", type=Path, required=True, help="new directory for this run's evidence")
    parser.add_argument("--unity", type=Path, default=Path(os.environ.get("UNITY_EDITOR", "")),
                        help="Unity Editor executable; alternatively set UNITY_EDITOR")
    parser.add_argument("--timeout", type=int, default=900, help="Unity build timeout in seconds")
    parser.add_argument("--expect-marker", help="optional regex to verify after the build")
    parser.add_argument("--marker-slot", default="all", help="slot 0-3 or all for --expect-marker")
    parser.add_argument("--marker-timeout", type=int, default=60)
    args = parser.parse_args()

    project = args.project.resolve()
    unity = args.unity.expanduser()
    scene = Path(args.scene)
    if not project.is_dir() or not (project / "Assets").is_dir():
        parser.error("--project must name a Unity project")
    if (not args.scene.startswith("Assets/") or scene.suffix.lower() != ".unity"
            or ".." in scene.parts or not (project / scene).is_file()):
        parser.error("--scene must name a saved .unity file under the project's Assets/")
    if not unity.is_file():
        parser.error("--unity or UNITY_EDITOR must name the Unity executable")
    if args.timeout < 1 or args.marker_timeout < 0:
        parser.error("timeouts must be nonnegative and Unity timeout must be positive")
    url = os.environ.get("VRCRT_BRIDGE_URL")
    pin = os.environ.get("VRCRT_CERT_SHA256")
    if not url or not re.fullmatch(r"[0-9a-fA-F]{64}", pin or ""):
        parser.error("set VRCRT_BRIDGE_URL and VRCRT_CERT_SHA256 to the paired Bridge")

    output = args.output_dir.expanduser().resolve()
    output.mkdir(mode=0o700, parents=True, exist_ok=False)
    cli = Path(__file__).with_name("vrcrt.sh")
    environment = dict(os.environ)
    environment["VRC_REMOTE_TEST_BRIDGE_URL"] = url
    environment["VRC_REMOTE_TEST_CERT_SHA256"] = pin
    environment["VRC_REMOTE_TEST_SCENE_PATH"] = args.scene
    if not environment.get("VRC_REMOTE_TEST_TOKEN"):
        if environment.get("VRCRT_TOKEN"):
            environment["VRC_REMOTE_TEST_TOKEN"] = environment["VRCRT_TOKEN"]
        else:
            token = subprocess.run(
                ["/usr/bin/security", "find-generic-password", "-s", "VRCRemoteTest.LanDirect",
                 "-a", url, "-w"], capture_output=True, text=True)
            if token.returncode:
                raise RuntimeError("paired Bridge token is missing from macOS Keychain")
            environment["VRC_REMOTE_TEST_TOKEN"] = token.stdout.strip()
    environment["VRCRT_TOKEN"] = environment["VRC_REMOTE_TEST_TOKEN"]
    if args.expect_marker:
        before = run_cli(cli, environment, "cursor")
        (output / "before.cursor.json").write_text(before, encoding="utf-8")

    editor_log = output / "unity-editor.log"
    console_log = output / "unity-console.log"
    command = [str(unity), "-batchmode", "-buildTarget", "Win64", "-projectPath", str(project),
               "-executeMethod", "VRCRemoteTest.RemoteBuildCommand.ExecuteRemoteBuildHeadless",
               "-logFile", str(editor_log)]
    print("Building and deploying %s..." % args.scene, flush=True)
    with console_log.open("w", encoding="utf-8") as stream:
        try:
            completed = subprocess.run(command, env=environment, stdout=stream, stderr=subprocess.STDOUT,
                                       timeout=args.timeout)
        except subprocess.TimeoutExpired:
            raise RuntimeError("Unity timed out; see %s" % editor_log)
    if completed.returncode:
        raise RuntimeError("Unity exited %d; see %s" % (completed.returncode, editor_log))
    build_ids = re.findall(r"\[VRC Remote Test\] Upload complete\. Build ID: ([0-9A-Za-z_-]+)",
                           editor_log.read_text(encoding="utf-8", errors="replace"))
    if len(build_ids) != 1:
        raise RuntimeError("Unity did not report exactly one build ID; see %s" % editor_log)
    build_id = build_ids[0]
    result = run_cli(cli, environment, "result", build_id)
    (output / "build-result.json").write_text(result, encoding="utf-8")
    status = run_cli(cli, environment, "status", "--json")
    (output / "clients.json").write_text(status, encoding="utf-8")
    for client in json.loads(status).get("clients", []):
        if client.get("running") and client.get("watchWorlds") is False:
            print("warning: slot %s is not using --watch-worlds; deployment does not prove a reload" %
                  client.get("slot"), file=sys.stderr)
    exports = run_cli(cli, environment, "export-log", "all", str(output))
    for path in exports.splitlines():
        print("Saved %s" % path)
    if args.expect_marker:
        matched = run_cli(cli, environment, "wait-marker", args.marker_slot, args.expect_marker,
                          str(args.marker_timeout), "--after", "@" + str(output / "before.cursor.json"))
        (output / "matched-markers.txt").write_text(matched, encoding="utf-8")
    print("Deployed build %s; evidence: %s" % (build_id, output))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, RuntimeError, ValueError) as error:
        print("vrcrt-run: %s" % error, file=sys.stderr)
        sys.exit(1)
