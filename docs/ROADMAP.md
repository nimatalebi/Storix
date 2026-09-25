# Storix roadmap

This is the prioritized backlog. A checked item exists in the code today.
Every item follows the [definition of done](../CONTRIBUTING.md#definition-of-done).

## Done (V1)

- [x] Windows service, WinForms manager (jobs, history, logs, settings, service control)
- [x] Sources: files (wildcard excludes, skip locked files), SQL Server (`.bak`, `CHECKSUM`, `COPY_ONLY`, `VERIFYONLY`), MongoDB (`mongodump --archive`, `--oplog`)
- [x] Destinations: local/UNC folder, FTP/FTPS, SFTP (password or key, host key pinning), Google Drive (service account, Shared Drives)
- [x] Schedules: daily, weekly, cron, with time zones
- [x] ZIP compression, AES-256-CBC + HMAC-SHA256 encryption (PBKDF2), SHA-256 checksums
- [x] Retention (keep last N / N days), retry with back-off, byte-level upload resume, atomic `.partial` uploads, partial-upload cleanup, upload size check, archive verification
- [x] Concurrent jobs, crash recovery, SQLite metadata with DPAPI-protected secrets
- [x] Import/export configurations, decrypt tool, e-mail notifications
- [x] About and feedback pages (GitHub issues and e-mail)

## P0: make V1 solid

- [x] Build and test on Windows in CI; warnings are errors
- [x] Integration tests with Testcontainers: atmoz/sftp, FTP, SQL Server, MongoDB replica set (backup → restore → byte compare)
- [x] Fault-injection tests: service killed mid-run, network drop + resume, disk full, cancellation, corrupted/truncated archives
- [x] Large-file test above 4 GB (ZIP64 + encryption, opt-in with `STORIX_LARGE_TESTS=1`) and files that change while archived
- [x] Scheduler tests around DST changes and time zones; optional catch-up of missed runs
- [x] MSI installer (WiX), GitHub Releases on `v*` tags, portable ZIP, SHA256SUMS
- [ ] Signed binaries (the workflow signs automatically once the `STORIX_SIGN_CERT` / `STORIX_SIGN_PASSWORD` secrets are set)

## P1: missing essentials

- [x] Amazon S3 and S3-compatible destinations (MinIO, Wasabi, R2, B2) with provider presets
- [x] Chunked archives (split volumes) with chunk-level resume
- [x] Restore from a destination or a local folder (restore wizard in the manager)
- [x] Google Drive OAuth sign-in for personal "My Drive"
- [x] Webhook notifications (JSON, HMAC-signed)
- [x] VSS snapshots for open and locked files
- [x] Dead man's switch: alert when a job has not succeeded for X hours; ping healthchecks.io / Uptime Kuma
- [x] Scheduled restore drills (for SQL: restore to a temp database + `DBCC CHECKDB`; MongoDB: `mongorestore --dryRun`)
- [x] SQL Server differential and log backups with chain tracking; point-in-time restore
- [x] Direct database restore from the UI (`RESTORE ... WITH MOVE`, `mongorestore`)
- [x] Bandwidth limit per destination and allowed upload windows per job
- [x] Pause uploads on metered connections
- [x] Pre/post job hooks (PowerShell/cmd) with timeout and exit-code handling
- [x] Cancel a running job from the UI
- [x] Pause and resume a running job (at the next step or volume)
- [x] Free-space check that fails early, with an estimate (staging and local destinations)
- [x] GFS retention (daily/weekly/monthly/yearly)
- [x] Key management: printable recovery sheet, optional key file, warning until the password backup is confirmed

## P2: new ideas

### Security and ransomware protection
- [x] Asymmetric mode (RSA-4096 public key on the agent, private key offline)
- [x] Immutable storage (S3 Object Lock); refuse to delete locked sets
- [x] Run under a gMSA or a low-privilege account, with a setup wizard
- [x] Audit log of configuration changes (who, when, what; secrets masked)
- [x] Windows password confirmation before restore, delete, export with secrets and service changes

### Storage efficiency
- [x] zstd compression
- [ ] Content-defined chunking and deduplication (incremental-forever)
- [ ] Incremental file backups using a file index
- [x] Build once and upload to many destinations; copy jobs for the 3-2-1 rule

### More sources
- [x] PostgreSQL, MySQL/MariaDB, Redis, SQLite
- [x] IIS configuration, registry keys, scheduled tasks, certificates
- [x] Docker volumes, Hyper-V VMs (Export-VM)

### More destinations
- [x] Azure Blob, OneDrive/SharePoint, Dropbox, WebDAV/Nextcloud, SMB with explicit credentials (Backblaze B2 via S3 or rclone)
- [x] rclone adapter
- [x] Presets for S3-compatible providers

### Restore experience
- [x] Browse and search inside a backup; restore single files
- [x] Standalone restore CLI (`storix-<version>-x64.exe`) that needs no install and no database
- [x] Restore preview (size, file count)

### Monitoring and UX
- [x] Tray icon with status and "run now"
- [x] Telegram / Bale / Slack / Teams / Discord notifications
- [x] Weekly summary e-mail (and chat channels)
- [x] Dashboard: storage per destination, growth trend, forecast, success rate
- [x] Prometheus metrics, Windows Event Log entries, OpenTelemetry
- [ ] Persian (RTL) and English UI, dark mode
- [x] Job templates and dry-run mode
- [x] First-run wizard

### Architecture and ecosystem
- [ ] Named-pipe or gRPC API between the UI and the service (replaces database polling)
- [x] CLI: `storix jobs | run | history | cancel | drill | restore | verify | list-files | decrypt | export | import`
- [x] Config as code (JSON job files with `${env:NAME}` secrets, `validate` and `apply` commands)
- [ ] Plugin system for sources and destinations
- [x] Circuit breaker per destination; job chains
- [ ] Auto-update from GitHub Releases with signature check

## P3: long term

- [ ] Central management server (many agents, web dashboard, RBAC)
- [ ] Linux agent (systemd)
- [ ] Optional hosted storage
