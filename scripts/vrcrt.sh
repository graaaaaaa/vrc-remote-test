#!/usr/bin/env bash
# Compatibility entry point. The CLI lives in the Unity package, package/Tools~/vrcrt.sh, so
# that Unity users get it with the package; this path keeps working for existing callers.
# Arguments, standard input and output, and the exit code are passed through unchanged.
set -u
self="${BASH_SOURCE[0]}"
# Follow links to this file (e.g. one put in ~/bin), so the package path is found from its real place.
while [ -L "$self" ]; do
    target="$(readlink "$self")"
    case "$target" in
        /*) self="$target" ;;
        *) self="$(dirname "$self")/$target" ;;
    esac
done
# CDPATH cleared: an inherited one would make cd print the folder into $here.
here="$(CDPATH= cd -P -- "$(dirname -- "$self")" && pwd)" || exit 1
exec bash "$here/../package/Tools~/vrcrt.sh" "$@"
