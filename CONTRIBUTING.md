# Contributing to KeepstoneBackup

Thank you for helping. KeepstoneBackup is released under the MIT License. By contributing, you agree that your contribution is licensed the same way.

## Ground rules

- All namespaces start with `NT.KeepstoneBackup`. The product name is **KeepstoneBackup** and the authors are **KeepstoneBackup Contributors**.
- `NT.KeepstoneBackup.Core` must stay free of Windows-only APIs, unless the call is guarded by an `OperatingSystem.IsWindows()` check.
- Keep the backup format backward compatible. When the format changes, bump the version byte (for example in `AesFileEncryptor`) and keep reading old versions.
- Never log secrets (passwords, connection strings, keys). Mark secret properties with `[Secret]`.
- Every change that touches encryption, archiving, retention or restore needs tests.
- Work in small, reviewable pull requests. Build and test after each step.

## Workflow

```powershell
dotnet build KeepstoneBackup.sln
dotnet test KeepstoneBackup.sln
```

1. Open an issue first for larger changes, so the design can be discussed.
2. Create a branch, make the change, and add or update tests and docs.
3. Open a pull request that describes what changed and why.

## Definition of done

Code, tests, and README/docs are updated. No secrets appear in logs. The backup format stays backward compatible.

## Contact

- Issues: https://github.com/nimatalebi/Storix/issues
- E-mail: nimatweb@gmail.com
