# Builds adblock_shim.dll (x64, release) and copies it to AJBrowser.Core/Native/, where the C# projects pick it up.
# Needs Rust (rustup) with the x86_64-pc-windows-gnu toolchain; see README.md. Run from any folder:
#   powershell -ExecutionPolicy Bypass -File native\adblock-shim\build.ps1
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$cargo = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
if (-not (Test-Path $cargo)) { $cargo = 'cargo' }

Push-Location $here
try {
    & $cargo test --target x86_64-pc-windows-gnu
    if ($LASTEXITCODE -ne 0) { throw "cargo test failed ($LASTEXITCODE)" }
    & $cargo build --release --locked --target x86_64-pc-windows-gnu
    if ($LASTEXITCODE -ne 0) { throw "cargo build failed ($LASTEXITCODE)" }
} finally {
    Pop-Location
}

$dll = Join-Path $here 'target\x86_64-pc-windows-gnu\release\adblock_shim.dll'
$destDir = Join-Path $here '..\..\AJBrowser.Core\Native'
New-Item -ItemType Directory -Force $destDir | Out-Null
Copy-Item $dll $destDir -Force
$hash = (Get-FileHash $dll -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Copied adblock_shim.dll ($([math]::Round((Get-Item $dll).Length / 1MB, 2)) MB) to AJBrowser.Core\Native"
Write-Host "SHA-256: $hash  (update THIRD-PARTY-NOTICES.md)"
