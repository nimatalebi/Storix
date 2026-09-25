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
- [ ] MSI installer (WiX), GitHub Releases, signed binaries

## P1: missing essentials

- [ ] Amazon S3 and S3-compatible destinations (MinIO, Wasabi, R2, B2)
- [ ] Chunked archives (split volumes) with chunk-level resume
- [ ] Restore from a destination or a local folder (engine done; wizard in the manager pending)
- [ ] Google Drive OAuth sign-in for personal "My Drive"
- [ ] Webhook notifications
- [ ] VSS snapshots for open and locked files
- [ ] Dead man's switch: alert when a job has not succeeded for X hours; ping healthchecks.io / Uptime Kuma
- [ ] Scheduled restore drills (for SQL: restore to a temp database + `DBCC CHECKDB`)
- [ ] SQL Server differential and log backups with chain tracking; point-in-time restore
- [ ] Direct database restore from the UI (`RESTORE ... WITH MOVE`, `mongorestore`; engine done)
- [ ] Bandwidth limit, allowed upload windows, pause on metered connections
- [ ] Pre/post job hooks (PowerShell/cmd) with timeout and exit-code handling
- [ ] Cancel or pause a running job from the UI
- [ ] Free-space check that fails early, with an estimate
- [ ] GFS retention (daily/weekly/monthly/yearly)
- [ ] Key management: printable recovery sheet, optional key file

## P2: new ideas

### Security and ransomware protection
- [ ] Asymmetric mode (public key on the agent, private key offline)
- [ ] Immutable storage (S3 Object Lock); refuse to delete locked sets
- [ ] Run under a gMSA or a low-privilege account, with a setup wizard
- [ ] Audit log of configuration changes
- [ ] Confirmation (TOTP / Windows Hello) before restore or delete

### Storage efficiency
- [ ] zstd compression
- [ ] Content-defined chunking and deduplication (incremental-forever)
- [ ] Incremental file backups using a file index
- [ ] Build once and upload to many destinations; copy jobs for the 3-2-1 rule

### More sources
- [ ] PostgreSQL, MySQL/MariaDB, Redis, SQLite
- [ ] IIS configuration, registry keys, scheduled tasks, certificates
- [ ] Docker volumes, Hyper-V VMs

### More destinations
- [ ] Azure Blob, OneDrive/SharePoint, Dropbox, Backblaze B2, WebDAV/Nextcloud, SMB with explicit credentials
- [ ] rclone adapter; presets for S3-compatible providers

### Restore experience
- [ ] Browse and search inside a backup; restore single files
- [ ] Standalone restore CLI (`storix-restore`) that needs no install and no database
- [ ] Restore preview (size, file count, estimated time)

### Monitoring and UX
- [ ] Tray icon with status and "run now"
- [ ] Telegram / Bale / Slack / Teams notifications; weekly summary e-mail
- [ ] Dashboard: storage per destination, growth trend, success rate
- [ ] Prometheus metrics, Windows Event Log entries, OpenTelemetry
- [ ] Persian (RTL) and English UI, dark mode
- [ ] Job templates and a first-run wizard; dry-run mode

### Architecture and ecosystem
- [ ] Named-pipe or gRPC API between the UI and the service (replaces database polling)
- [ ] CLI: `storix list | run | status | restore | verify | export | import`
- [ ] Config as code (YAML/JSON job files with a validate command)
- [ ] Plugin system for sources and destinations
- [ ] Circuit breaker per destination; job chains
- [ ] Auto-update from GitHub Releases with signature check

## P3: long term

- [ ] Central management server (many agents, web dashboard, RBAC)
- [ ] Linux agent (systemd)
- [ ] Optional hosted storage
