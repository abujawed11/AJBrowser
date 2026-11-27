# adblock-shim

A thin C wrapper around [Brave's adblock-rust](https://github.com/brave/adblock-rust) (MPL-2.0), so AJBrowser's C#
code can call it with P/Invoke (Phase 8). The C# side is `AJBrowser.Core/Blocking/` (`NativeMethods.cs`,
`AdblockRustEngine.cs`).

**You only need this folder to change the wrapper or update adblock-rust.** The built `adblock_shim.dll` is committed
at `AJBrowser.Core/Native/adblock_shim.dll`, so building AJBrowser needs no Rust.

## Build

1. Install Rust once (per user, no admin):
   download `rustup-init.exe` for `x86_64-pc-windows-gnu` from https://rustup.rs, check its `.sha256`, then
   `rustup-init.exe -y --default-host x86_64-pc-windows-gnu --default-toolchain stable --profile minimal`.
   The GNU toolchain brings its own linker, so Visual Studio's C++ workload isn't needed. The DLL only imports
   Windows system DLLs.
2. `powershell -ExecutionPolicy Bypass -File native\adblock-shim\build.ps1`
   (runs the Rust tests, builds the release DLL, copies it to `AJBrowser.Core/Native/`, prints its SHA-256).
3. Run the C# tests (`dotnet test AJBrowser.Core.Tests`), and update the version/SHA-256 in `THIRD-PARTY-NOTICES.md`.

## Updating adblock-rust

Change the pinned version (`adblock = { version = "=x.y.z", … }`) in `Cargo.toml`, `cargo update -p adblock`, rebuild.
A new engine version may refuse engine files saved by the old one; AJBrowser then rebuilds from the lists.

## Interface rules

- Strings are UTF-8 pointer + length in, `AdbBytes` out (freed with `adb_bytes_free`).
- Every function catches Rust panics; none can unwind into .NET.
- `adb_abi_version()` must equal `NativeMethods.ExpectedAbiVersion`; bump both when the interface changes.
- The `single-thread` feature is off, so the engine can be built on one thread and used from any other.
