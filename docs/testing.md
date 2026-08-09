# Testing

## Credentials

The real SSH test configuration is `C:\Users\wulda\.ssh\ssh-config.json`. It may contain local aliases and secret material and must never be committed, copied into test output, or printed in logs. Passwords imported by the MVP application remain in memory only; persisted profiles contain only a `SecretReference` identifier.

Normal automated tests use in-memory stores, streams, and protocol logic only. Real SSH/SFTP interoperability tests use a test-only loader, HTML-decode password values, and immediately place them in an `InMemorySecretStore`. They are opt-in and must never print configuration values or passwords.

## VNC target

The VNC interoperability target is `10.10.0.4:5900` using RFB 3.8 with the `None` security type. The opt-in test negotiates the standard 32bpp little-endian true-color pixel format, requests only Raw encoding, and reads at least one framebuffer update within a 20-second timeout.

## Real integration tests

xUnit 2.9 includes `SkipException.ForSkip`, but its API documentation states that runtime skipping requires the xUnit v3 runner. These tests therefore use a discovery-time `FactAttribute.Skip` unless `TERMSQUARED_RUN_INTEGRATION` is exactly `1`. CI and normal local runs safely report the network tests as skipped without reading local credentials or contacting private targets.

```powershell
$env:TERMSQUARED_RUN_INTEGRATION = '1'
dotnet test tests\TermSquared.Protocols.Tests\TermSquared.Protocols.Tests.csproj --configuration Release
Remove-Item Env:TERMSQUARED_RUN_INTEGRATION
```

An opted-in run treats an unavailable or changed target as a real interoperability failure. J4125 and rock must first report an unknown host key that is stored in a temporary known-host file, then report the identical algorithm and SHA256 fingerprint as trusted on the second connection. SSH command execution must return `TERMSQUARED_OK`, SFTP must list `/`, and J4125 must accept an interactive PTY command and return `TERMSQUARED_PTY_OK`. YC8G is expected to throw `SshAuthenticationException`; that expected exception is a passing known-fact test.

Safe result records may contain only these fields:

```text
SSH alias=<alias> outcome=<success|authentication-failed> hostKeyAlgorithm=<algorithm> hostKeyFingerprint=SHA256:<base64>
VNC target=10.10.0.4:5900 outcome=<success|failure-category> size=<width>x<height> encoding=Raw
```

Do not record usernames, passwords, password lengths, raw host keys, command stderr that may contain environment details, or the contents of the SSH configuration file.

## Verified interoperability

The Windows MVP was verified on 2026-08-10:

- `J4125`: SSH exec, trusted-host reconnect, SFTP `/`, and interactive PTY passed.
- `rock`: SSH exec, trusted-host reconnect, and SFTP `/` passed.
- `YC8G`: TCP and Host Key negotiation succeeded; the configured password was rejected as expected.
- `10.10.0.4:5900`: RFB 3.8 None authentication succeeded and a 2560x1440 Raw framebuffer update was read.

These are environment facts, not embedded product credentials. Passwords and raw configuration remain outside the repository.

## Commands

```powershell
dotnet restore TermSquared.slnx
dotnet build TermSquared.slnx --configuration Release --no-restore
dotnet test TermSquared.slnx --configuration Release --no-build
```

Square is checked out as the `external/Square` submodule. Use `git clone --recurse-submodules` or run `git submodule update --init --recursive` before building.
