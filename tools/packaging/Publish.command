#!/bin/zsh
set -euo pipefail
script_dir="$(cd -P -- "$(dirname -- "$0")" && pwd)"
project_root="$(cd -P -- "$script_dir/../.." && pwd)"
runtime="${1:-osx-arm64}"
arguments=(run --project "$script_dir/OpusMac.Packaging.csproj" -c Release -- publish "$project_root" "$runtime")
if (( $# > 1 )); then arguments+=("$2"); fi
exec dotnet "${arguments[@]}"

