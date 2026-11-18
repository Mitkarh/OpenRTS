#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_HOME="$PWD/.dotnet-home"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export NUGET_PACKAGES="$PWD/.packages"

godot_bin="${OPENRTS_GODOT:-}"
if [[ -z "$godot_bin" ]]; then
  if command -v godot >/dev/null 2>&1; then godot_bin="$(command -v godot)"
  else godot_bin="$PWD/../Godot_mono.app/Contents/MacOS/Godot"; fi
fi

build() {
  local target="${1:-OpenRTS.sln}"
  local configuration="${2:-Debug}"
  if [[ -n "${OPENRTS_NUGET_SOURCE:-}" ]]; then
    dotnet restore "$target" --disable-build-servers -m:1 --source "$OPENRTS_NUGET_SOURCE"
  else
    dotnet restore "$target" --disable-build-servers -m:1
  fi
  dotnet build "$target" --configuration "$configuration" --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
}

import_assets() {
  "$godot_bin" --headless --editor --path "$PWD/${1:-sandbox}" --import
}

build_map() {
  local scene="$1" output
  shift
  if ! output="$("$godot_bin" --headless --editor --path "$PWD/sandbox" --max-fps 60 --quit-after 7200 "$scene" -- "--build-map=$scene" "$@")"; then
    printf '%s\n' "$output" >&2
    return 1
  fi
  printf '%s\n' "$output"
  if ! rg -q '^OPENRTS_MAP_BUILD:' <<< "$output"; then
    echo 'Map authoring did not report a published build.' >&2
    return 1
  fi
}

action="${1:-build}"
if [[ $# -gt 0 ]]; then shift; fi
case "$action" in
  build) build ;;
  test)
    build
    dotnet run --project tests/OpenRTS.Network.Tests --no-build --no-launch-profile
    dotnet run --project tests/DeclarativeContent.Tests --no-build --no-launch-profile
    dotnet run --project tests/OpenRTS.Tests --no-build --no-launch-profile -- "$@"
    ;;
  match-directory) build tools/OpenRTS.MatchDirectory/OpenRTS.MatchDirectory.csproj; exec dotnet run --project tools/OpenRTS.MatchDirectory --no-build --no-launch-profile -- "$@" ;;
  headless) build; dotnet run --project tools/OpenRTS.Headless --no-build --no-launch-profile -- "$@" ;;
  benchmark)
    build tools/OpenRTS.Headless/OpenRTS.Headless.csproj Release
    DOTNET_TieredCompilation=0 dotnet run --project tools/OpenRTS.Headless --configuration Release --no-build --no-launch-profile -- --benchmark "$@"
    ;;
  run) build; import_assets; exec "$godot_bin" --path "$PWD/sandbox" -- "$@" ;;
  editor) build; exec "$godot_bin" --path "$PWD/sandbox" --editor ;;
  run3d|editor3d|verify3d)
    build sandbox3d/OpenRTS.Sandbox3D.csproj
    if [[ "$action" == editor3d ]]; then
      exec "$godot_bin" --path "$PWD/sandbox3d" --editor "$@"
    fi
    import_assets sandbox3d
    if [[ "$action" == verify3d ]]; then
      verification_output="$("$godot_bin" --headless --max-fps 60 --quit-after 600 --path "$PWD/sandbox3d" -- --verify)"
      printf '%s\n' "$verification_output"
      if ! grep -q '^Sandbox3D verified:' <<< "$verification_output"; then
        echo 'Sandbox3D did not report a completed verification.' >&2
        exit 1
      fi
      exit 0
    fi
    exec "$godot_bin" --path "$PWD/sandbox3d" -- "$@"
    ;;
  map-build|scene-map)
    if [[ "${1:-}" != res://*.tscn || ( "$action" == scene-map && $# -ne 1 ) ]]; then
      echo 'Usage: tools/dev.sh map-build res://path/to/map.tscn [--format=compiled|bundle] [--verify [--start=slot ...] [--start-set=path]]' >&2
      echo '       tools/dev.sh scene-map res://path/to/map.tscn' >&2
      exit 2
    fi
    if ! rg -q '^enabled=.*"res://addons/openrts_editor/plugin.cfg"' sandbox/project.godot; then
      echo 'Enable OpenRTS Map Authoring before building a map.' >&2
      exit 1
    fi
    if [[ "$action" == scene-map ]]; then set -- "$1" --format=bundle; fi
    build sandbox/OpenRTS.Sandbox.csproj; import_assets
    build_map "$@"
    ;;
  scene-map-pack)
    if [[ $# -ne 1 || "$1" != /*.pck ]]; then
      echo 'Usage: tools/dev.sh scene-map-pack /absolute/output.pck' >&2
      exit 2
    fi
    build; import_assets
    "$godot_bin" --headless --path "$PWD/sandbox" -- --check-scene-maps
    staging="$(mktemp -d "$(dirname "$1")/.scene-map-pack.XXXXXX")"
    trap 'rm -rf -- "$staging"' EXIT
    if ! "$godot_bin" --headless --editor --path "$PWD/sandbox" --export-pack "Scene Map Pack" "$staging/maps.pck" >"$staging/export.log" 2>&1; then
      cat "$staging/export.log" >&2
      exit 1
    fi
    if rg -q '^ERROR:' "$staging/export.log" || [[ ! -s "$staging/maps.pck" ]]; then
      cat "$staging/export.log" >&2
      exit 1
    fi
    mv -f -- "$staging/maps.pck" "$1"
    printf '%s\n' "$1"
    ;;
  verify-authoring)
    if ! grep -q '^enabled=.*"res://addons/openrts_editor/plugin.cfg"' sandbox/project.godot; then
      echo 'Enable OpenRTS Map Authoring in Sandbox Project Settings > Plugins before verify-authoring.' >&2
      exit 1
    fi
    build sandbox/OpenRTS.Sandbox.csproj; import_assets
    "$godot_bin" --headless --path "$PWD/sandbox" res://Verification/MapAuthoringChecks.tscn
    "$godot_bin" --headless --editor --path "$PWD/sandbox" res://examples/maps/AuthoringMap.tscn -- --verify-map-editor
    build_map res://examples/maps/RockyPass.tscn --format=compiled --verify
    build_map res://examples/maps/AuthoringMap.tscn --format=bundle --verify --start=west --start-set=content/start-sets/infantry.yaml
    if build_map res://examples/maps/RockyPass.tscn --format=bundle; then
      echo '2D authoring incorrectly published a scene-map bundle.' >&2
      exit 1
    fi
    ;;
  verify-wan)
    build
    if [[ "${1:-}" == "--netem" ]]; then
      shift
      docker build -t openrts-netem:net2 -f tools/network/Dockerfile tools/network
      python3 tools/verify_wan_netem.py "$@"
    else
      dotnet run --project tests/OpenRTS.Network.Tests --no-build --no-launch-profile -- --wan "$@"
    fi
    ;;
  verify-directory) build; import_assets; python3 tools/verify_directory.py "$godot_bin" "$@" ;;
  verify-replay) build; import_assets; python3 tools/verify_replay.py "$godot_bin" "$@" ;;
  verify-menu)
    build; import_assets
    if [[ "${1:-}" == "--visible" ]]; then
      shift; exec "$godot_bin" --path "$PWD/sandbox" -- --verify-menu "$@"
    else
      exec "$godot_bin" --headless --path "$PWD/sandbox" -- --verify-menu "$@"
    fi
    ;;
  verify-network) build; import_assets; python3 tools/verify_network.py "$godot_bin" "$@" ;;
  verify) "$0" test; import_assets; python3 tools/verify.py "$godot_bin" ;;
  *) echo 'Usage: tools/dev.sh [build|test|headless|benchmark|run|editor|run3d|editor3d|verify3d|map-build|scene-map|scene-map-pack|verify|verify-authoring|verify-network|verify-menu|verify-replay|verify-wan|match-directory|verify-directory]' >&2; exit 2 ;;
esac
