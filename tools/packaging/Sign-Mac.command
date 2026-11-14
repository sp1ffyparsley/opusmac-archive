#!/bin/zsh
# Creates a new signed package. It never changes the original extracted package.
set -euo pipefail
[[ "$(uname -s)" == Darwin ]] || { print -u2 "Signing requires macOS."; exit 2; }
[[ $# -ge 1 && $# -le 3 ]] || {
    print -u2 'Usage: Sign-Mac.command --adhoc [NEW_OUTPUT_FOLDER]'
    print -u2 '   or: Sign-Mac.command "Developer ID Application: Name (TEAM)" [NEW_OUTPUT_FOLDER] [NOTARY_KEYCHAIN_PROFILE]'
    exit 2
}
identity="$1"
signing_mode=developer_identity
timestamp_arguments=(--timestamp)
runtime_arguments=(--options runtime)
hardened_runtime=true
if [[ "$identity" == --adhoc ]]; then
    (( $# <= 2 )) || { print -u2 "Ad-hoc signing cannot be submitted for notarization."; exit 2; }
    identity=-
    signing_mode=adhoc
    timestamp_arguments=()
    runtime_arguments=()
    hardened_runtime=false
elif [[ "$identity" == "-" ]]; then
    print -u2 "Choose --adhoc explicitly for private local signing, or a real identity from your keychain."
    exit 2
fi
package_root="$(cd -P -- "$(dirname -- "$0")" && pwd)"
source_app="$package_root/Directory Opus Mac.app"
[[ -d "$source_app" && -f "$package_root/SHA256SUMS" ]] || { print -u2 "Keep this script beside the original complete package."; exit 2; }
(
    cd -- "$package_root"
    [[ -z "$(find . -type l -print)" ]] || { print -u2 "Input package contains an unexpected symbolic link."; exit 1; }
    /usr/bin/shasum -a 256 -c SHA256SUMS >/dev/null
    /usr/bin/cmp \
        <(/usr/bin/sed -E 's/^[[:xdigit:]]{64}  //' SHA256SUMS | LC_ALL=C /usr/bin/sort) \
        <(find . -type f ! -path './SHA256SUMS' | /usr/bin/sed 's|^\./||' |
          /usr/bin/awk '!/(^|\/)\.DS_Store$/ || /^Directory Opus Mac\.app\//' | LC_ALL=C /usr/bin/sort)
)
output="${2:-$package_root-signed-$(date -u +%Y%m%dT%H%M%SZ)}"
[[ "$output" != *$'\n'* && "$output" != *$'\r'* ]] || { print -u2 "Output path cannot contain a line break."; exit 2; }
output_parent="$(cd -P -- "$(dirname -- "$output")" && pwd)"
output="$output_parent/$(basename -- "$output")"
case "$output/" in "$package_root/"*) print -u2 "Signed output must be outside the original package."; exit 2;; esac
[[ ! -e "$output" ]] || { print -u2 "Refusing to overwrite existing signing output: $output"; exit 2; }
mkdir -- "$output"
output="$(cd -P -- "$output" && pwd)"
signed_app="$output/Directory Opus Mac.app"
print "Signing output: $output"
/usr/bin/ditto "$source_app" "$signed_app"
cp -- "$package_root/Verify-Mac.command" "$output/Verify-Mac.command"
cp -- "$package_root/MAC-ACCEPTANCE.md" "$output/MAC-ACCEPTANCE.md"
cp -- "$package_root/release-manifest.json" "$output/unsigned-build-manifest.json"
/usr/bin/ditto "$package_root/ThirdPartyLicenses" "$output/ThirdPartyLicenses"
chmod 755 "$output/Verify-Mac.command"
# Sign native components first. Never use --deep to perform signing.
while IFS= read -r -d '' native_file; do
    case "$native_file" in
        *.dylib)
            /usr/bin/codesign --force "${runtime_arguments[@]}" "${timestamp_arguments[@]}" --sign "$identity" "$native_file"
            ;;
        */OpusMac|*/OpusMac.Checks)
            /usr/bin/codesign --force "${runtime_arguments[@]}" "${timestamp_arguments[@]}" --entitlements "$package_root/OpusMac.entitlements" --sign "$identity" "$native_file"
            ;;
    esac
done < <(find "$signed_app/Contents/MacOS" -type f -print0)
/usr/bin/codesign --force "${runtime_arguments[@]}" "${timestamp_arguments[@]}" --entitlements "$package_root/OpusMac.entitlements" --sign "$identity" "$signed_app"
/usr/bin/codesign --verify --deep --strict --verbose=2 "$signed_app" > "$output/signature-verification.txt" 2>&1
notarized=false
if (( $# == 3 )); then
    # Credentials stay in the named keychain profile. They are never placed in this script.
    submission="$output.notarization-upload.zip"
    [[ ! -e "$submission" ]] || { print -u2 "Notarization upload already exists: $submission"; exit 2; }
    /usr/bin/ditto -c -k --keepParent "$signed_app" "$submission"
    /usr/bin/xcrun notarytool submit "$submission" --keychain-profile "$3" --wait --output-format json > "$output/notarization.json"
    [[ "$(/usr/bin/plutil -extract status raw -o - "$output/notarization.json")" == Accepted ]] || {
        print -u2 "Apple did not accept notarization. Inspect $output/notarization.json"
        exit 1
    }
    /usr/bin/xcrun stapler staple "$signed_app" > "$output/stapler.txt" 2>&1
    /usr/bin/xcrun stapler validate "$signed_app" >> "$output/stapler.txt" 2>&1
    /usr/sbin/spctl --assess --type execute --verbose=2 "$signed_app" > "$output/gatekeeper.txt" 2>&1
    notarized=true
    rm -- "$submission"
fi
cat > "$output/signing-receipt.json" <<EOF
{
  "schema": "opusmac_signing_v1",
  "signing_mode": "$signing_mode",
  "hardened_runtime": $hardened_runtime,
  "code_signature_verified": true,
  "notarized_and_stapled": $notarized,
  "native_runtime_acceptance_complete": false,
  "source_manifest": "unsigned-build-manifest.json",
  "current_integrity_baseline": "SHA256SUMS"
}
EOF
/usr/bin/plutil -insert signing_identity -string "$identity" "$output/signing-receipt.json"
cat > "$output/README.txt" <<EOF
Directory Opus Mac — signed Apple Silicon package

The unsigned-build-manifest.json records the input build. Signing changes native
binaries and adds signature resources; SHA256SUMS records this signed package.
Native runtime acceptance has not been assumed. Run Verify-Mac.command --launch
from Terminal and complete MAC-ACCEPTANCE.md before using valuable files.
Notarized, stapled, and Gatekeeper-assessed: $notarized
Signing mode: $signing_mode
Hardened runtime requested: $hardened_runtime
Ad-hoc signing does not establish a Developer ID identity or notarization.
Gatekeeper and folder-permission decisions remain active.

The original extracted build remains unchanged.
EOF
(
    cd -- "$output"
    while IFS= read -r -d '' file; do
        relative="${file#./}"
        case "$relative" in
            .DS_Store|*/.DS_Store)
                [[ "$relative" == "Directory Opus Mac.app/"* ]] || continue
                ;;
        esac
        /usr/bin/shasum -a 256 "$relative"
    done < <(find . -type f ! -path './SHA256SUMS' -print0)
) > "$output/SHA256SUMS"
(cd -- "$output" && /usr/bin/shasum -a 256 -c SHA256SUMS >/dev/null)
archive="$output.tar.gz"
[[ ! -e "$archive" ]] || { print -u2 "Archive already exists: $archive"; exit 2; }
/usr/bin/tar -czf "$archive" -C "$(dirname -- "$output")" "$(basename -- "$output")"
/usr/bin/shasum -a 256 "$archive" > "$archive.sha256"
print "Signed package and SHA-256 archive: $archive"
print "Mac execution still requires Verify-Mac.command and the acceptance checklist."
