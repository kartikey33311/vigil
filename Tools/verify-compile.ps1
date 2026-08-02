# Vigil - headless compile gate.
#
# Runs the Unity Editor in batch mode, forces a full script compile, and fails on
# any C# error. Same command CI runs, so a green local run means a green CI run.
#
#   .\Tools\verify-compile.ps1
#   .\Tools\verify-compile.ps1 -Clean      # nuke Library first (slow, authoritative)
#   .\Tools\verify-compile.ps1 -RunTests   # also run EditMode tests
#
# Exit codes: 0 clean | 1 compile errors | 2 Unity missing/failed | 3 tests failed
#
# NOTE: this file is deliberately ASCII-only and avoids '#' inside "$( )"
# interpolations. Windows PowerShell 5.1 reads a BOM-less UTF-8 file as ANSI, and
# a '#' inside a subexpression starts a comment that swallows the closing quote.

[CmdletBinding()]
param(
    [string]$UnityVersion = "6000.0.25f1",
    [string]$ProjectPath  = "",
    [switch]$Clean,
    [switch]$RunTests,
    [int]$TimeoutMinutes = 40
)

$ErrorActionPreference = "Stop"

# $PSScriptRoot is not reliably populated inside a param() default on Windows
# PowerShell 5.1, so it is resolved here instead.
if ([string]::IsNullOrEmpty($ProjectPath))
{
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $ProjectPath = Split-Path -Parent $scriptDir
}

$unityExe = Join-Path "C:\Program Files\Unity\Hub\Editor" "$UnityVersion\Editor\Unity.exe"
if (-not (Test-Path $unityExe))
{
    Write-Host "Unity $UnityVersion not found at $unityExe" -ForegroundColor Red
    exit 2
}

$logDir = Join-Path $ProjectPath "Logs"
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Force -Path $logDir | Out-Null }

$stamp   = Get-Date -Format "yyyyMMdd-HHmmss"
$logFile = Join-Path $logDir "compile-$stamp.log"
$testXml = Join-Path $logDir "editmode-$stamp.xml"

if ($Clean)
{
    $library = Join-Path $ProjectPath "Library"
    if (Test-Path $library)
    {
        Write-Host "Removing Library/ for a cold compile..." -ForegroundColor Yellow
        Remove-Item -Recurse -Force $library
    }
}

Write-Host "Project : $ProjectPath"
Write-Host "Unity   : $UnityVersion"
Write-Host "Log     : $logFile"

if ($RunTests)
{
    $unityArgs = @(
        "-batchmode", "-nographics", "-silent-crashes",
        "-projectPath", $ProjectPath,
        "-logFile", $logFile,
        "-runTests", "-testPlatform", "EditMode", "-testResults", $testXml
    )
}
else
{
    $unityArgs = @(
        "-batchmode", "-quit", "-nographics", "-silent-crashes",
        "-projectPath", $ProjectPath,
        "-logFile", $logFile
    )
}

Write-Host "Launching Unity (minutes on a cold Library)..." -ForegroundColor Cyan
$proc = Start-Process -FilePath $unityExe -ArgumentList $unityArgs -PassThru -NoNewWindow

# Touching .Handle forces PowerShell to keep the native process handle open.
# Without this, Start-Process -PassThru releases it and $proc.ExitCode reads back
# as $null after the process ends - which is exactly how this gate previously
# reported a broken build as clean.
$null = $proc.Handle

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while (-not $proc.HasExited)
{
    if ((Get-Date) -gt $deadline)
    {
        Write-Host "Timed out after $TimeoutMinutes minutes - killing Unity." -ForegroundColor Red
        try { $proc.Kill() } catch { }
        exit 2
    }
    Start-Sleep -Seconds 5
}

# WaitForExit() with no timeout also flushes the exit code onto the object.
# Without it $proc.ExitCode can come back empty, which previously made the
# exit-status check silently useless.
$proc.WaitForExit()
$unityExit = $proc.ExitCode
if ($null -eq $unityExit) { $unityExit = -1 }

Write-Host ("Unity exited with code {0}" -f $unityExit) -ForegroundColor Cyan

if (-not (Test-Path $logFile))
{
    Write-Host "No log produced - Unity failed to start." -ForegroundColor Red
    exit 2
}

$log = Get-Content $logFile -Raw

$errorPattern = '(?m)^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\):\s*error\s+(?<code>CS\d+):\s*(?<msg>.+)$'
$warnPattern  = '(?m)^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\):\s*warning\s+(?<code>CS\d+):\s*(?<msg>.+)$'

# Unity may compile an assembly MORE THAN ONCE in a single batchmode session:
# when it imports new .cs files it compiles, reloads the domain, then compiles
# again with the new files present. Errors from the first pass stay in the log
# even though the final pass succeeded. Scraping the whole log therefore reports
# phantom failures on exactly the runs where you added a file.
#
# The EXIT CODE is the authoritative verdict on the final state. An earlier
# version of this script matched on the "Aborting batchmode" banner instead —
# but Unity writes that to stderr, not to -logFile, so the check never fired and
# a genuinely broken build was reported as clean. Never trust the log for
# pass/fail; trust it only for the error DETAIL.
$buildFailed = ($unityExit -ne 0)

$errors   = [regex]::Matches($log, $errorPattern)
$warnings = [regex]::Matches($log, $warnPattern)

# Deduplicate: Unity reports the same error once per referencing assembly.
$unique = @{}
foreach ($m in $errors)
{
    $key = "{0}|{1}|{2}" -f $m.Groups['file'].Value, $m.Groups['line'].Value, $m.Groups['code'].Value
    if (-not $unique.ContainsKey($key))
    {
        $unique[$key] = [pscustomobject]@{
            file    = $m.Groups['file'].Value
            line    = [int]$m.Groups['line'].Value
            column  = [int]$m.Groups['col'].Value
            code    = $m.Groups['code'].Value
            message = $m.Groups['msg'].Value
        }
    }
}

$uniqueErrors = @($unique.Values)

# Errors in the log but a zero exit code means a later pass resolved them.
if ($uniqueErrors.Count -gt 0 -and -not $buildFailed)
{
    Write-Host ("NOTE: {0} error line(s) came from an earlier compile pass and were resolved by a later one (Unity exited 0)." -f $uniqueErrors.Count) -ForegroundColor DarkYellow
    $uniqueErrors = @()
}

# Non-zero exit with nothing parseable is still a failure - do not report clean.
if ($buildFailed -and $uniqueErrors.Count -eq 0)
{
    Write-Host "BUILD FAILED - Unity exited $unityExit with no parseable CS errors. Check the log:" -ForegroundColor Red
    Write-Host "  $logFile" -ForegroundColor Red
    exit 1
}

if ($uniqueErrors.Count -eq 0)
{
    Write-Host ("COMPILE CLEAN - 0 errors, {0} warning instances." -f $warnings.Count) -ForegroundColor Green

    if ($RunTests -and (Test-Path $testXml))
    {
        [xml]$xml = Get-Content $testXml
        $run = $xml.DocumentElement
        $total   = $run.GetAttribute("total")
        $passed  = $run.GetAttribute("passed")
        $failed  = $run.GetAttribute("failed")
        $skipped = $run.GetAttribute("skipped")

        Write-Host ""
        Write-Host ("TESTS: total={0} passed={1} failed={2} skipped={3}" -f $total, $passed, $failed, $skipped) -ForegroundColor Cyan

        if ([int]$failed -gt 0)
        {
            foreach ($case in $xml.SelectNodes("//test-case[@result='Failed']"))
            {
                $caseName = $case.GetAttribute("fullname")
                Write-Host ("  FAIL {0}" -f $caseName) -ForegroundColor Red
            }
            exit 3
        }
    }

    exit 0
}

Write-Host ("COMPILE FAILED - {0} unique error(s)." -f $uniqueErrors.Count) -ForegroundColor Red
Write-Host ""

Write-Host "BY ERROR CODE:" -ForegroundColor Yellow
$uniqueErrors | Group-Object code | Sort-Object Count -Descending | ForEach-Object {
    $sample = $_.Group[0].message
    if ($sample.Length -gt 95) { $sample = $sample.Substring(0, 95) }
    Write-Host ("  {0,-8} x{1,-4} {2}" -f $_.Name, $_.Count, $sample)
}

Write-Host ""
Write-Host "BY FILE:" -ForegroundColor Yellow
$uniqueErrors | Group-Object file | Sort-Object Count -Descending | Select-Object -First 25 | ForEach-Object {
    Write-Host ("  {0,-4} {1}" -f $_.Count, $_.Name)
}

Write-Host ""
Write-Host "ERRORS:" -ForegroundColor Yellow
$uniqueErrors | Sort-Object file, line | Select-Object -First 60 | ForEach-Object {
    Write-Host ("  {0}({1}): {2}: {3}" -f $_.file, $_.line, $_.code, $_.message) -ForegroundColor Red
}

$jsonOut = Join-Path $logDir "compile-errors-$stamp.json"
$uniqueErrors | ConvertTo-Json -Depth 3 | Out-File -FilePath $jsonOut -Encoding utf8

Write-Host ""
Write-Host ("Structured errors: {0}" -f $jsonOut) -ForegroundColor Cyan
exit 1
