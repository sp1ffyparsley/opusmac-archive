#!/bin/zsh
# Verification keeps the extracted package unchanged. Every file-operation fixture
# belongs to a fresh physical folder in the current user's home directory.
set -euo pipefail
package_root="$(cd -P -- "$(dirname -- "$0")" && pwd)"
cd -- "$package_root"
[[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]] || {
    print -u2 "Run this verifier in a native Apple Silicon macOS Terminal."
    exit 2
}
verification_parent="$HOME/Library/Logs/OpusMac/Verification"
mkdir -p -- "$verification_parent"
verification_parent="$(cd -P -- "$verification_parent" && pwd)"
run_root="$(mktemp -d "$verification_parent/run-XXXXXXXX")"
run_root="$(cd -P -- "$run_root" && pwd)"
app="$package_root/Directory Opus Mac.app"
payload="$app/Contents/MacOS"
hashes=false
architecture=false
core_started=false
core_passed=false
core_all_verified=false
ui_passed=false
signature_verified=false
desktop_launch_accepted=false
completed=false
function receipt_on_exit {
    local result=$?
    trap - EXIT
    cat > "$run_root/verification.json" <<EOF
{
  "schema": "opusmac_macos_acceptance_v1",
  "utc": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "platform": "macOS",
  "architecture": "arm64",
  "package_hashes_verified": $hashes,
  "native_architecture_verified": $architecture,
  "core_suite_executed": $core_started,
  "core_suite_passed": $core_passed,
  "all_core_checks_verified": $core_all_verified,
  "headless_ui_self_test_passed": $ui_passed,
  "code_signature_verified": $signature_verified,
  "desktop_launch_request_accepted": $desktop_launch_accepted,
  "manual_desktop_acceptance_complete": false,
  "complete_directory_opus_parity_verified": false,
  "automatic_verification_completed": $completed,
  "exit_code": $result
}
EOF
    print "Verification receipt and retained disposable fixtures: $run_root"
    exit "$result"
}
trap receipt_on_exit EXIT
function run_bounded {
    local limit="$1" log="$2"
    shift 2
    "$@" > "$log" 2>&1 &
    local owned_pid=$! began=$SECONDS
    while kill -0 "$owned_pid" 2>/dev/null; do
        if (( SECONDS - began >= limit )); then
            kill -TERM "$owned_pid" 2>/dev/null || true
            sleep 1
            kill -KILL "$owned_pid" 2>/dev/null || true
            wait "$owned_pid" 2>/dev/null || true
            print -u2 "Verification process exceeded ${limit}s; inspect $log"
            return 124
        fi
        sleep 0.2
    done
    local result=0
    wait "$owned_pid" || result=$?
    if (( result != 0 )); then
        print -u2 "Verification process failed ($result); inspect $log"
    fi
    return "$result"
}
[[ -f SHA256SUMS && -d "$app" ]] || { print -u2 "Keep the verifier beside its complete extracted package."; exit 2; }
# Package names cannot contain a newline. Builder and archive verifier enforce this.
if [[ -n "$(find . -type l -print)" ]]; then
    print -u2 "Unexpected symbolic link in the package."; exit 1
fi
/usr/bin/shasum -a 256 -c SHA256SUMS > "$run_root/package-hashes.txt"
/usr/bin/sed -E 's/^[[:xdigit:]]{64}  //' SHA256SUMS | LC_ALL=C /usr/bin/sort > "$run_root/expected-files.txt"
# Finder may add .DS_Store beside the app. The sealed app's full inventory stays exact.
find . -type f ! -path './SHA256SUMS' | /usr/bin/sed 's|^\./||' |
    /usr/bin/awk '!/(^|\/)\.DS_Store$/ || /^Directory Opus Mac\.app\//' |
    LC_ALL=C /usr/bin/sort > "$run_root/actual-files.txt"
/usr/bin/cmp "$run_root/expected-files.txt" "$run_root/actual-files.txt"
hashes=true
/usr/bin/plutil -lint "$app/Contents/Info.plist" > "$run_root/plist.txt"
[[ "$(/usr/bin/plutil -extract CFBundleExecutable raw -o - "$app/Contents/Info.plist")" == OpusMac ]]
[[ "$(/usr/bin/plutil -extract CFBundleIdentifier raw -o - "$app/Contents/Info.plist")" == io.github.sp1ffyparsley.opusmac ]]
for executable in "$payload/OpusMac" "$payload/OpusMac.Checks"; do
    [[ -x "$executable" ]] || { print -u2 "Missing executable permission: $executable. Extract the supplied tar.gz again."; exit 1; }
done
while IFS= read -r -d '' native_file; do
    case "$native_file" in
        *.dylib|*/OpusMac|*/OpusMac.Checks)
            native_description="$(/usr/bin/file -b "$native_file")"
            print "$native_file: $native_description" >> "$run_root/architecture.txt"
            [[ "$native_description" == *arm64* ]] || { print -u2 "Native file lacks arm64: $native_file"; exit 1; }
            ;;
    esac
done < <(find "$payload" -type f -print0)
architecture=true
if /usr/bin/codesign --verify --deep --strict --verbose=2 "$app" > "$run_root/signature.txt" 2>&1; then
    signature_verified=true
fi
core_started=true
run_bounded 300 "$run_root/core-output.txt" "$payload/OpusMac.Checks" "$run_root/Core"
core_passed=true
core_all_verified="$(/usr/bin/plutil -extract all_requested_checks_verified raw -o - "$run_root/Core/checks.json")"
[[ "$core_all_verified" == true ]] || { print -u2 "Core fixtures contain unverified cases; inspect Core/checks.json."; exit 1; }
run_bounded 180 "$run_root/ui-output.txt" "$payload/OpusMac" --self-test --state-dir "$run_root/UI" --render-check "$run_root/UI-render"
ui_passed=true
completed=true
print "Automatic checks passed. Native windows, permissions, external volumes, and Mac shortcuts still need the acceptance checklist."
if [[ "${1:-}" == --launch ]]; then
    mkdir -p -- "$run_root/Manual/Source" "$run_root/Manual/Destination"
    print 'Disposable verification file.' > "$run_root/Manual/Source/Read me.txt"
    /usr/bin/open -n -a "$app" --args --state-dir "$run_root/Interactive-profile" "$run_root/Manual/Source" "$run_root/Manual/Destination"
    desktop_launch_accepted=true
    print "A native desktop launch was requested for disposable folders. Check its actual behavior against MAC-ACCEPTANCE.md."
fi
