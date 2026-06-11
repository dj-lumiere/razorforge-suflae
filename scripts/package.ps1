#Requires -Version 7.0
# RazorForge packaging script (Windows host -> win-x64 package).
# Run with PowerShell 7+:  pwsh -File scripts\package.ps1
# (Windows PowerShell 5.1 lacks utf8NoBOM and other features this script uses.)
#
# Produces: dist\razorforge-v<version>-win-x64.zip (+ sha256 in checksums.txt)
#
# IMPORTANT: packages the HOST platform only — the native runtime is built by the
# host toolchain, so cross-RID publishing would bundle the wrong native binaries.
# Build the Linux package on Linux with scripts/package.sh.
#
# Prerequisites: .NET 10 SDK; LLVM/clang + CMake + Ninja (native runtime build).
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$Rid = 'win-x64'
$Version = if ($env:VERSION) { $env:VERSION } else {
    (Select-String -Path 'RazorForge.csproj' -Pattern '<Version>(.*)</Version>').Matches[0].Groups[1].Value
}
$Name = "razorforge-v$Version-$Rid"
$Out = "dist\$Name"

Write-Host "=== RazorForge $Version -> $Name ==="
if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
if (Test-Path "dist\$Name.zip") { Remove-Item -Force "dist\$Name.zip" }
New-Item -ItemType Directory -Force -Path dist | Out-Null

Write-Host '=== build native runtime first ==='
# The csproj's Content globs for native\build\bin|lib are evaluated BEFORE the
# BuildNativeLibraries target runs during publish — on a fresh checkout the
# directories don't exist yet and publish would silently ship no native
# artifacts. Build them up front so the globs see real files.
Push-Location native
cmd /c build.bat
$nativeExit = $LASTEXITCODE
Pop-Location
if ($nativeExit -ne 0) { throw "native build failed ($nativeExit)" }

Write-Host '=== dotnet publish (self-contained) ==='
dotnet publish RazorForge.csproj -c Release -r $Rid --self-contained true -o $Out --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

Write-Host '=== flatten native runtime artifacts (installed layout) ==='
# `dotnet publish` keeps Content items under their source-relative paths
# (native\build\bin|lib). The installed layout — and the compiler's own P/Invoke
# probing + buildandrun's prebuilt-layout detection — expect them FLAT next to
# the executable, matching the dev bin/ output.
foreach ($sub in 'native\build\bin', 'native\build\lib') {
    $src = Join-Path $Out $sub
    if (Test-Path $src) {
        Get-ChildItem $src -File | Move-Item -Destination $Out -Force
    }
}
if (Test-Path "$Out\native") { Remove-Item -Recurse -Force "$Out\native" }

Write-Host '=== prune dev-only artifacts ==='
foreach ($dir in 'RazorForge-Wiki', 'Suflae-Wiki') {
    if (Test-Path "$Out\$dir") { Remove-Item -Recurse -Force "$Out\$dir" }
}
Get-ChildItem $Out -Recurse -Filter '*.pdb' | Remove-Item -Force
Copy-Item LICENSE, README.md $Out

# Short alias next to the canonical binary (apphost stub is small; payload DLLs are
# shared in-directory). Users preferring `forge` can alias it themselves — that name
# is deliberately not shipped to avoid colliding with Foundry's `forge`.
Copy-Item "$Out\RazorForge.exe" "$Out\rf.exe"

Write-Host '=== bundle self-contained LLVM toolchain (llvm-mingw + opt) ==='
# llvm-mingw gives a fully redistributable clang + ld.lld + mingw CRT/import libs,
# so produced executables link without Visual Studio or the Windows SDK. The
# compiler resolves <package>\toolchain\bin before PATH (see ResolveToolchainTool).
$LlvmMingwTag = '20260602'   # llvm-mingw release built from LLVM 22.1.7
$LlvmVersion  = '22.1.7'
$CacheDir = 'dist\_cache'
New-Item -ItemType Directory -Force $CacheDir | Out-Null

$mingwZip = Join-Path $CacheDir "llvm-mingw-$LlvmMingwTag-ucrt-x86_64.zip"
if (-not (Test-Path $mingwZip)) {
    curl.exe -L -o $mingwZip "https://github.com/mstorsjo/llvm-mingw/releases/download/$LlvmMingwTag/llvm-mingw-$LlvmMingwTag-ucrt-x86_64.zip"
    if ($LASTEXITCODE -ne 0) { throw 'llvm-mingw download failed' }
}
$mingwRoot = Join-Path $CacheDir 'llvm-mingw-root'
if (-not (Test-Path "$mingwRoot\bin\clang.exe")) {
    tar -xf $mingwZip -C $CacheDir
    if ($LASTEXITCODE -ne 0) { throw 'llvm-mingw extract failed' }
    if (Test-Path $mingwRoot) { Remove-Item -Recurse -Force $mingwRoot }
    Rename-Item (Join-Path $CacheDir "llvm-mingw-$LlvmMingwTag-ucrt-x86_64") 'llvm-mingw-root'
}

# llvm-mingw ships no `opt`; cherry-pick it from the official LLVM release of the
# same version (msvc build — statically linked, standalone binary).
$optExe = Join-Path $CacheDir "opt-$LlvmVersion.exe"
if (-not (Test-Path $optExe)) {
    $llvmTar = Join-Path $CacheDir "clang+llvm-$LlvmVersion-x86_64-pc-windows-msvc.tar.xz"
    if (-not (Test-Path $llvmTar)) {
        curl.exe -L -o $llvmTar "https://github.com/llvm/llvm-project/releases/download/llvmorg-$LlvmVersion/clang+llvm-$LlvmVersion-x86_64-pc-windows-msvc.tar.xz"
        if ($LASTEXITCODE -ne 0) { throw 'LLVM release download failed' }
    }
    tar -xf $llvmTar -C $CacheDir "clang+llvm-$LlvmVersion-x86_64-pc-windows-msvc/bin/opt.exe"
    if ($LASTEXITCODE -ne 0) { throw 'opt.exe extract failed' }
    Move-Item (Join-Path $CacheDir "clang+llvm-$LlvmVersion-x86_64-pc-windows-msvc\bin\opt.exe") $optExe
    Remove-Item -Recurse -Force (Join-Path $CacheDir "clang+llvm-$LlvmVersion-x86_64-pc-windows-msvc")
}

# Prune to the subset buildandrun needs: clang (driver) + ld.lld (linker) + the
# shared LLVM DLLs they load, the compiler-rt builtins, and the x86_64 mingw
# sysroot (CRT objects + import libraries + runtime DLLs). lldb, clangd, headers,
# python, and the other target triples are dead weight.
$tc = Join-Path $Out 'toolchain'
New-Item -ItemType Directory -Force "$tc\bin" | Out-Null
# clang.exe is a tiny launcher that execs clang-22 (the real driver) from its
# own directory — both are needed.
foreach ($f in 'clang.exe', 'clang-22.exe', 'ld.lld.exe', 'libLLVM-22.dll',
              'libclang-cpp.dll', 'libc++.dll', 'libunwind.dll',
              'libwinpthread-1.dll', 'libffi-8.dll') {
    Copy-Item "$mingwRoot\bin\$f" "$tc\bin\"
}
Copy-Item $optExe "$tc\bin\opt.exe"
New-Item -ItemType Directory -Force "$tc\lib\clang\22\lib\windows" | Out-Null
Copy-Item "$mingwRoot\lib\clang\22\lib\windows\libclang_rt.builtins-x86_64.a" "$tc\lib\clang\22\lib\windows\"
Copy-Item -Recurse "$mingwRoot\x86_64-w64-mingw32" "$tc\x86_64-w64-mingw32"
if (Test-Path "$tc\x86_64-w64-mingw32\share") { Remove-Item -Recurse -Force "$tc\x86_64-w64-mingw32\share" }
Copy-Item "$mingwRoot\LICENSE.TXT" "$tc\LICENSE.llvm-mingw.TXT"

Write-Host '=== add install script + quickstart + AI reference ==='
Copy-Item "$PSScriptRoot\package-assets\install.ps1" $Out
Copy-Item "$PSScriptRoot\package-assets\install.cmd" $Out
Copy-Item "$PSScriptRoot\package-assets\QUICKSTART.md" $Out
Copy-Item "$PSScriptRoot\..\RAZORFORGE-FOR-AI.md" $Out

Write-Host '=== smoke test: self-contained buildandrun (system toolchain hidden) ==='
& "$Out\RazorForge.exe" version
if ($LASTEXITCODE -ne 0) { throw 'smoke version failed' }
$smokeDir = Join-Path ([System.IO.Path]::GetTempPath()) "rf_pkg_smoke_$PID"
New-Item -ItemType Directory -Force -Path $smokeDir | Out-Null
@'
module PackageSmoke

import IO/Console

routine start()
  show("packaged razorforge works")
  return
'@ | Set-Content -Path "$smokeDir\smoke.rf" -Encoding utf8NoBOM
# Stage a copy of the package OUTSIDE the repo (buildandrun's dev-checkout
# detection walks up from the exe and would find the repo's native/build tree
# from dist\), strip PATH down to the OS so the bundled toolchain is the only
# one visible, and run from the smoke directory — exactly an end user's setup.
$stageDir = Join-Path ([System.IO.Path]::GetTempPath()) "rf_pkg_stage_$PID"
if (Test-Path $stageDir) { Remove-Item -Recurse -Force $stageDir }
Copy-Item -Recurse $Out $stageDir
$rfExe = Join-Path $stageDir 'RazorForge.exe'
$oldPath = $env:PATH
Push-Location $smokeDir
try {
    $env:PATH = "$env:SystemRoot\System32;$env:SystemRoot"
    $smokeOut = & $rfExe buildandrun smoke.rf 2>&1 | Out-String
} finally {
    $env:PATH = $oldPath
    Pop-Location
    Remove-Item -Recurse -Force $stageDir
}
if ($smokeOut -notmatch 'packaged razorforge works') {
    Write-Host $smokeOut
    throw 'self-contained buildandrun smoke failed'
}
Write-Host 'self-contained buildandrun OK'
Remove-Item -Recurse -Force $smokeDir

Write-Host '=== archive + checksum ==='
Compress-Archive -Path $Out -DestinationPath "dist\$Name.zip" -Force
$hash = (Get-FileHash -Algorithm SHA256 "dist\$Name.zip").Hash.ToLowerInvariant()
Add-Content -Path 'dist\checksums.txt' -Value "$hash  $Name.zip"
Write-Host "Packaged: dist\$Name.zip"
