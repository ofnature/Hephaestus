<#
.SYNOPSIS
    Builds raphael-cli from source at a pinned tag and stages it for bundling into the plugin zip.

.DESCRIPTION
    raphael-rs publishes no prebuilt CLI (its releases are the GUI only), so we build it ourselves:
    a known binary at a known commit. Output goes to Hephaestus/Tools/raphael/, which the csproj
    copies next to Hephaestus.dll and DalamudPackager zips with everything else.

    Requires Rust (rustup) and the MSVC C++ build tools. raphael-rs pins its own nightly toolchain
    in rust-toolchain.toml; rustup fetches it automatically on first build.

    Rust lives on D: on the dev machine (RUSTUP_HOME / CARGO_HOME are user environment variables).
    If they are not set this script falls back to rustup's defaults.

.PARAMETER Tag
    raphael-rs git tag to build. Bump deliberately: re-run the simulator's Raphael vector tests
    and re-check the CLI's arguments afterwards (docs/artisan-notes.md has the invocation).
#>
param(
    [string]$Tag = 'v0.28.6'
)

$ErrorActionPreference = 'Stop'

$repoRoot  = Split-Path -Parent $PSScriptRoot
$source    = Join-Path $repoRoot '.build\raphael-rs'
$stageDir  = Join-Path $repoRoot 'Hephaestus\Tools\raphael'

if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    $cargoBin = if ($env:CARGO_HOME) { Join-Path $env:CARGO_HOME 'bin' } else { Join-Path $env:USERPROFILE '.cargo\bin' }
    if (-not (Test-Path (Join-Path $cargoBin 'cargo.exe'))) {
        throw "cargo not found. Install Rust from https://rustup.rs (set RUSTUP_HOME and CARGO_HOME first to keep it off C:)."
    }
    $env:PATH = "$cargoBin;$env:PATH"
}

# git writes progress to stderr, which Windows PowerShell 5.1 turns into errors — so no 2>&1 here.
$ErrorActionPreference = 'Continue'
if (Test-Path (Join-Path $source '.git')) {
    git -C $source fetch --quiet --depth 1 origin "refs/tags/${Tag}:refs/tags/${Tag}"
    git -C $source checkout --quiet --force $Tag
} else {
    New-Item -ItemType Directory -Force (Split-Path -Parent $source) | Out-Null
    git clone --quiet --depth 1 --branch $Tag https://github.com/KonaeAkira/raphael-rs.git $source
}
if ($LASTEXITCODE -ne 0) { throw "git failed for raphael-rs $Tag" }

$commit = (git -C $source rev-parse HEAD).Trim()
Write-Host "raphael-rs $Tag ($commit)"

Push-Location $source
try {
    cargo build -p raphael-cli --release
    if ($LASTEXITCODE -ne 0) { throw "cargo build failed" }
} finally {
    Pop-Location
}
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force $stageDir | Out-Null
Copy-Item (Join-Path $source 'target\release\raphael-cli.exe') (Join-Path $stageDir 'raphael-cli.exe') -Force
Copy-Item (Join-Path $source 'LICENSE') (Join-Path $stageDir 'LICENSE-raphael-rs.txt') -Force

@"
raphael-cli
===========
Built from source: https://github.com/KonaeAkira/raphael-rs
Tag:    $Tag
Commit: $commit

raphael-rs is Copyright its authors and licensed under the Apache License, Version 2.0.
The full licence text is in LICENSE-raphael-rs.txt next to this file. This binary is an
unmodified build of the raphael-cli crate; Hephaestus runs it as a separate process to
obtain a baseline crafting rotation.
"@ | Set-Content -Path (Join-Path $stageDir 'NOTICE-raphael-rs.txt') -Encoding utf8

$exe = Get-Item (Join-Path $stageDir 'raphael-cli.exe')
Write-Host ("staged {0} ({1:N1} MB) -> {2}" -f $exe.Name, ($exe.Length / 1MB), $stageDir)
