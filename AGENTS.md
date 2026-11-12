# Project guidance

- WagTerm is an independent fork of agwinterm. Preserve upstream attribution and the original MIT license.
- Public branding is `WagTerm`. Technical names remain `wagterm` and C# namespaces remain `Wagterm.*`.
- Keep runtime identities isolated: `wagterm.exe`, `wagtermctl`, `WAGTERM_*`, `WAG_*`, and the wagterm profile and pipes.
- The Win32 project is `Wagterm.Win32`, but its assembly name is `wagterm`. Friend-assembly declarations and reflection tests must use the assembly name where appropriate.
- Historical plans and reports retain upstream names, versions and issue references.

## Verification

- Managed build: `dotnet build Wagterm.slnx -c Release`.
- Managed tests: `dotnet test Wagterm.slnx -c Release -- xUnit.MaxParallelThreads=1`. The default parallel run can hit synchronization and ConPTY timeouts on this machine, including before the rebranding.
- Native build: `cargo build --release --manifest-path native/Cargo.toml --locked`.
- Native tests: `cargo test --release --manifest-path native/Cargo.toml --locked`.
- Build the native binaries before managed tests to exercise the Rust parity and host paths.
- The managed loader and native core must agree on ABI 20 and the `wagcore_*` exports.
- The Ralphex launcher fixtures require Go 1.26, matching CI. Go 1.25 cannot compile `os.Process.WithHandle`.
