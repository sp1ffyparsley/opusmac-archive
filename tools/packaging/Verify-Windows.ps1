[CmdletBinding()]
param([string]$VerificationParent)
$ErrorActionPreference = 'Stop'
$packageRoot = $PSScriptRoot
if (-not $VerificationParent) { $VerificationParent = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'OpusMac\Verification' }
$runRoot = Join-Path ([IO.Path]::GetFullPath($VerificationParent)) ('run-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $runRoot
$receipt = [ordered]@{
    schema = 'opusmac_windows_acceptance_v1'
    utc = [DateTimeOffset]::UtcNow.ToString('o')
    package_hashes_verified = $false
    packaged_core_suite_passed = $false
    all_core_checks_verified = $false
    headless_ui_self_test_passed = $false
    macos_runtime_executed = $false
    complete_directory_opus_parity_verified = $false
    automatic_verification_completed = $false
}
function Quote-NativeArgument([string]$Value) {
    if ($Value.IndexOf([char]0) -ge 0) { throw 'A native argument contains a null character.' }
    return '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}
function Invoke-Bounded([string]$Executable, [string[]]$Arguments, [int]$Seconds, [string]$Log) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $Executable
    $start.Arguments = (($Arguments | ForEach-Object { Quote-NativeArgument $_ }) -join ' ')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw "Could not start $Executable" }
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($Seconds * 1000)) {
            $process.Kill()
            throw "Verification timed out after $Seconds seconds: $Executable"
        }
        $text = $outputTask.GetAwaiter().GetResult() + [Environment]::NewLine + $errorTask.GetAwaiter().GetResult()
        [IO.File]::WriteAllText($Log, $text, (New-Object Text.UTF8Encoding $false))
        if ($process.ExitCode -ne 0) { throw "Verification exited $($process.ExitCode); inspect $Log" }
    } finally { $process.Dispose() }
}
try {
    $manifestPath = Join-Path $packageRoot 'release-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schema -ne 'opusmac_release_v1' -or $manifest.rid -ne 'win-x64' -or -not $manifest.self_contained) { throw 'Package identity mismatch.' }
    $expected = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.files) {
        if ([IO.Path]::IsPathRooted($file.path) -or $file.path -match '(^|/)\.\.?(/|$)|\\|[\r\n]') { throw "Unsafe manifest path: $($file.path)" }
        if (-not $expected.Add($file.path)) { throw "Duplicate manifest path: $($file.path)" }
        $path = Join-Path $packageRoot $file.path
        $item = Get-Item -LiteralPath $path -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $item.Length -ne $file.length -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "Package integrity mismatch: $($file.path)" }
    }
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Force) {
        $relative = $item.FullName.Substring($packageRoot.Length + 1).Replace('\','/')
        if ($relative -notin @('release-manifest.json','SHA256SUMS')) { $null = $seen.Add($relative) }
    }
    if (-not $expected.SetEquals($seen)) { throw 'The package contains unexpected or missing files.' }
    $receipt.package_hashes_verified = $true
    $payload = Join-Path $packageRoot 'Directory Opus Mac'
    Invoke-Bounded (Join-Path $payload 'OpusMac.Checks.exe') @((Join-Path $runRoot 'Core')) 300 (Join-Path $runRoot 'core-output.txt')
    $core = Get-Content -LiteralPath (Join-Path $runRoot 'Core\checks.json') -Raw | ConvertFrom-Json
    $receipt.packaged_core_suite_passed = $true
    $receipt.all_core_checks_verified = [bool]$core.all_requested_checks_verified
    Invoke-Bounded (Join-Path $payload 'OpusMac.exe') @('--self-test','--state-dir',(Join-Path $runRoot 'UI'),'--render-check',(Join-Path $runRoot 'UI-render')) 180 (Join-Path $runRoot 'ui-output.txt')
    $receipt.headless_ui_self_test_passed = $true
    $receipt.automatic_verification_completed = $true
    Write-Output "Packaged Windows checks passed; unverified fixture capabilities remain recorded: $($core.unverified)."
} finally {
    $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runRoot 'verification.json') -Encoding UTF8
    Write-Output "Verification receipt and disposable fixtures: $runRoot"
}

