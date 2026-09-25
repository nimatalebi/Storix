# Storix

**Storix** is an open-source backup agent for Windows.
A Windows service runs your backup jobs on a schedule. A Windows Forms app (**Storix Manager**) lets you set up jobs, see backup history and control the service.

Storix backs up **files and folders**, **SQL Server** databases (`.bak`), **MongoDB** (`mongodump`), **PostgreSQL**, **MySQL/MariaDB**, **Redis**, **SQLite** and **Windows server configuration** (IIS, registry, scheduled tasks, certificates). It can compress and encrypt each backup, then send it to a **local/UNC folder**, **FTP/FTPS**, **SFTP**, **Google Drive**, **Amazon S3 / S3-compatible storage** (Cloudflare R2, Wasabi, Backblaze B2, MinIO, Arvan…), **Azure Blob**, **WebDAV** (Nextcloud, NAS), **Dropbox**, **OneDrive / SharePoint**, a **Telegram or Bale** channel (directly or through a Cloudflare Worker relay) or any of the 40+ providers of **rclone**. Network shares can use their own credentials. It also retries failed steps, resumes interrupted uploads, verifies every backup, deletes old backups by your retention rules and keeps a full history in SQLite.

[![build](https://github.com/nimatalebi/Storix/actions/workflows/build.yml/badge.svg)](https://github.com/nimatalebi/Storix/actions/workflows/build.yml)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![License: MIT](https://img.shields.io/badge/license-MIT-green)

---

## Features

```
Backup Agent
│
├── Scheduler
│   ├── Daily
│   ├── Weekly
│   └── Custom Cron
│
├── Sources
│   ├── Files
│   ├── SQL Server
│   │   └── .bak
│   ├── MongoDB
│   │   └── mongodump
│   ├── PostgreSQL (pg_dump / pg_dumpall)
│   ├── MySQL / MariaDB (mysqldump)
│   ├── Redis (RDB snapshot)
│   ├── SQLite (online backup API)
│   ├── Windows system (IIS, registry, tasks, certificates)
│   ├── Docker volumes
│   └── Hyper-V virtual machines
│
├── Processing
│   ├── Compression
│   ├── Encryption
│   ├── Chunking (volumes)
│   └── Retention
│
├── Destinations
│   ├── Local Folder
│   ├── FTP
│   ├── SFTP
│   ├── Google Drive
│   ├── S3 / S3-compatible
│   ├── Azure Blob Storage
│   ├── WebDAV (Nextcloud, NAS)
│   ├── Dropbox
│   ├── OneDrive / SharePoint
│   ├── Telegram / Bale (optional Cloudflare Worker relay)
│   └── rclone (40+ providers)
│
└── Monitoring
    ├── Logs
    ├── Backup History
    ├── Success/Failure
    └── Notifications
```

| Capability | How Storix handles it |
|---|---|
| **Resume upload** | Uploads go to `<name>.partial` first. On retry, FTP and SFTP (and local folders) continue from the bytes already sent. Google Drive uses its resumable upload protocol. S3 uses multipart uploads; unfinished ones are aborted before a retry. |
| **Retry** | The database dump and each upload are retried with exponential back-off (attempt count, first delay and multiplier are set per job). |
| **Checksum** | A SHA-256 hash is saved in the history and uploaded as a `sha256sum`-compatible `.sha256` file next to each archive. |
| **Encryption** | AES-256-CBC with HMAC-SHA256 (encrypt-then-MAC). The key comes from your password via PBKDF2-SHA256 with 600,000 iterations, or from a random data key wrapped with an RSA-4096 public key (the private key stays offline). Encryption streams data, so large files are fine. |
| **Immutable backups** | S3 Object Lock (governance or compliance, N days) on new backups. Retention never deletes a locked backup; it is kept and logged. |
| **Retention** | "Keep last N" and/or "delete older than N days", plus long-term GFS rules (keep daily/weekly/monthly/yearly), applied on every destination. The newest backup is never deleted. |
| **Concurrent jobs** | Jobs run in parallel up to a global limit. The same job never runs twice at once. |
| **Chunking** | Optionally split backups into volumes (`.part0001`, `.part0002`…) with a manifest. Each volume is uploaded and checked separately; after an interruption, only the missing volumes are uploaded again. |
| **Open files (VSS)** | Optionally read files from a Volume Shadow Copy snapshot, so open and locked files (PST, databases, VM disks) are backed up consistently. |
| **Large files** | Everything is streamed. ZIP64 is supported. No step loads a whole file into memory. |
| **Crash recovery** | At startup, runs left in progress are marked *Interrupted* and leftover temporary files are removed. A scheduled run that was missed while the machine or service was off runs once at startup (can be turned off per job). The service restarts automatically if it fails. |
| **Partial upload cleanup** | Leftover `.partial` files from interrupted runs are deleted from destinations. |
| **Database consistency** | SQL Server: `BACKUP DATABASE … WITH CHECKSUM` (COPY_ONLY by default, so your existing backup chain is left intact). MongoDB: optional `--oplog` for a point-in-time dump of a replica set. |
| **SQL Server chains** | Full, differential and transaction-log backups. LSNs are recorded for every backup, and **Tools → SQL Server point-in-time restore** rebuilds the chain and restores to the latest state or to any moment (`STOPAT`). |
| **Backup verification** | Optional `RESTORE VERIFYONLY`. Every archive can be re-read (and decrypted) before upload. The size of each uploaded file is checked. |
| **Notifications & monitoring** | E-mail, webhooks (JSON, HMAC-signed), Telegram, Bale, Slack, Teams and Discord. A dead man's switch alerts when a job has no successful backup for N hours. healthchecks.io and Uptime Kuma pings are supported. |
| **Observability** | Optional Prometheus `/metrics` endpoint (last success, size, duration and status per job), Windows Event Log entries for warnings and errors, and OpenTelemetry traces exported over OTLP. |
| **Bandwidth** | Upload limit per destination (KB/s) and an optional daily upload window per job (e.g. 22:00-06:00). |
| **Job chains** | Run a job automatically after another job succeeds (e.g. copy files after a database dump). A destination that fails repeatedly is skipped for 30 minutes (circuit breaker). |
| **Incremental backups** | File jobs can back up only the files that changed (size or modification time) since the previous backup, with a new full backup every N days. Incremental archives are named `.inc.zip` and carry the list of deleted files. Restoring one restores its chain (full + incrementals) automatically, from a destination or from a folder; retention never deletes a backup that a kept incremental depends on. A failed or partial run makes the next backup a full one. |
| **Telegram / Bale** | Backups as documents in a private channel: files are split into 19 MB parts (so the bot can download them again) and listed in a pinned catalog, so another machine can restore. A Cloudflare Worker relay (`tools/telegram-relay`) helps when the server cannot reach Telegram; notifications can use it too. See [docs/TELEGRAM.md](docs/TELEGRAM.md). |
| **Deduplication** | Optional "incremental forever" mode: files are split into content-defined chunks (FastCDC), compressed with zstd and encrypted (AES-256-GCM, keyed chunk ids). Only chunks a destination does not have yet are uploaded, in ~32 MB pack files; each backup is a small `.snap` snapshot. Every restored file is checked against its SHA-256; retention deletes snapshots and then packs no snapshot uses. Restore needs the snapshot and its packs (from the destination, or all in one folder). |
| **Plugins** | Add your own sources and destinations as .NET class libraries in the `plugins` folder; their secret settings are encrypted like built-in ones. See [docs/PLUGINS.md](docs/PLUGINS.md) and the sample plugin. |
| **Copy jobs (3-2-1)** | A copy job replicates another job's backups, still encrypted, from one of its destinations to other destinations (e.g. NAS → S3). Only missing backups are copied, each is checked against its SHA-256 first, and the copy job has its own retention. Chain it to the source job to copy right after each backup. |
| **Hooks** | Commands before and after each backup (cmd/PowerShell), with timeout, exit-code handling and job variables. |
| **Control** | Run now, pause/resume or cancel a running backup, hold uploads on metered connections, and an early free-space check based on the previous backup size. |
| **Restore drills** | Scheduled or on-demand test restores of the latest backup. SQL Server backups are restored into a temporary database and checked with `DBCC CHECKDB`; MongoDB dumps are validated with `mongorestore --dryRun`. |
| **Audit log** | Every change to jobs and settings, every import/export, restore and service action is recorded with user, machine and a diff (secrets masked). See the **Audit** tab. |
| **Import / export configs** | Export all jobs and settings to JSON. Secrets are either removed or protected with a passphrase (AES-256-GCM). |

### V1 scope

- [x] Windows Service
- [x] Files
- [x] SQL Server
- [x] MongoDB
- [x] FTP
- [x] SFTP
- [x] Google Drive
- [x] Schedule (daily / weekly / cron)
- [x] Compression
- [x] AES encryption
- [x] Retention
- [x] Retry
- [x] SQLite metadata
- [x] Import / export backup configs

### Roadmap

The full, prioritized backlog is in [docs/ROADMAP.md](docs/ROADMAP.md). Highlights: S3, chunked archives, VSS, restore wizard, a standalone restore CLI, more notification channels and incremental backups.

---

## Architecture

```
┌──────────────────────┐         ┌───────────────────────────┐
│  Storix Manager      │         │  Storix Service           │
│  (WinForms, admin)   │         │  (Windows Service)        │
│                      │         │                           │
│  jobs / settings ────┼──┐   ┌──┼──► BackupScheduler        │
│  history / logs  ◄───┼─┐│   │┌─┼──── BackupJobRunner       │
│  "Run now" request ──┼┐││   │││ │   source → zip → AES →   │
└──────────────────────┘│││   │││ │   sha256 → upload →      │
                        ▼▼▼   ▼▼▼ │   verify → retention     │
                 %ProgramData%\Storix\storix.db (SQLite, WAL) │
                        └──────────┴──────────────────────────┘
```

The manager and the service share only the SQLite database. The manager saves job definitions and puts "run now" requests in a queue. The service checks the database every few seconds, starts jobs that are due or requested, and writes the history back.

### Solution layout

| Project | Description |
|---|---|
| `src/NT.Storix.Core` | The engine: models, scheduling (Cronos), sources, processing, destinations, SQLite repositories, secret protection, import/export. |
| `src/NT.Storix.Service` | Worker Service host that runs as the Windows service (`Storix.Service.exe`) or a systemd unit on Linux. Logs with Serilog to `%ProgramData%\Storix\logs` (`/var/lib/storix/logs`). |
| `src/NT.Storix.WinForms` | Storix Manager (`Storix.Manager.exe`), the desktop UI. |
| `src/NT.Storix.Cli` | `storix.exe` command-line tool (standalone restore, jobs, config as code). |
| `tests/NT.Storix.Core.Tests` | xUnit tests: encryption, archives, schedules, retention, persistence, import/export, restore and the full pipeline. |
| `samples/NT.Storix.Plugins.Sample` | Example source and destination plugin. |
| `tests/NT.Storix.IntegrationTests` | Docker-based tests (Testcontainers) against real SFTP, FTP, SQL Server and MongoDB servers. |

All namespaces start with `NT.` (for example `NT.Storix.Core.Engine`).

---

## Getting started

### Requirements

- Windows 10 / 11 or Windows Server 2016+
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build. Published builds are self-contained.
- For MongoDB backups: [MongoDB Database Tools](https://www.mongodb.com/try/download/database-tools) (`mongodump`)

### Build and test

```powershell
git clone https://github.com/nimatalebi/Storix.git
cd Storix
dotnet build Storix.sln
dotnet test Storix.sln
```

### Integration tests

These tests start real SFTP, FTP, S3 (LocalStack), Azure Blob (Azurite), SQL Server, MongoDB (replica set), PostgreSQL, MySQL and Redis servers in Docker, plus WebDAV and rclone (rclone must be installed). For each one they back up, restore and compare the data. They are opt-in:

```bash
STORIX_INTEGRATION_TESTS=1 dotnet test tests/NT.Storix.IntegrationTests
```

### Publish and install

The easiest way: download `Storix-<version>-x64.msi` from [GitHub Releases](https://github.com/nimatalebi/Storix/releases). The installer installs and starts the service and adds **Storix Manager** to the Start menu.

To build it yourself:

```powershell
# MSI + portable ZIP + SHA256SUMS in ./artifacts (Windows)
./build/package.ps1 -Version 1.0.0
```

Or publish without an installer:

```powershell
# Publish the service and the manager into one folder
./build/publish.ps1 -Output C:\Tools\Storix

# Start the manager (it asks for administrator rights)
C:\Tools\Storix\Storix.Manager.exe
```

In the manager, open the **Service** tab and click **Install**, then **Start**.
You can also install the service without the UI:

```powershell
./build/install-service.ps1 -Path C:\Tools\Storix\Storix.Service.exe
```

The service runs as `LocalSystem`, uses *Automatic (Delayed Start)* and restarts automatically if it fails.

### Linux agent (systemd)

The service and the CLI also run on Linux (x64) as a headless agent; jobs are managed with the CLI (config as code: `storix apply`, `storix run`, `storix history`…). Sources that need Windows (VSS, SQL Server on Windows paths, Hyper-V, Windows system) are not available there.

```sh
tar -xzf storix-<version>-linux-x64.tar.gz
sudo ./storix-<version>-linux-x64/install.sh      # installs to /opt/storix and starts storix.service
sudo storix apply my-jobs.json                    # add jobs; ${env:NAME} placeholders keep secrets out of the file
journalctl -u storix -f
```

Data, logs and the database live in `/var/lib/storix`. Secrets in the database are encrypted with AES-256-GCM using a random machine key (`/var/lib/storix/secret.key`, readable by root only). `sudo ./install.sh --uninstall` removes the program and keeps the data. Build the package yourself with `./build/package-linux.sh <version>`.

### Releases

Push a tag like `v1.2.0` and the `release` workflow builds the MSI and the portable ZIP, then publishes a GitHub release.
To sign the binaries, add the repository secrets `STORIX_SIGN_CERT` (base64-encoded `.pfx`) and `STORIX_SIGN_PASSWORD`.

### Development

To run the service as a normal console app with its own data folder:

```powershell
$env:STORIX_DATA_DIR = "$PWD\.data"
dotnet run --project src/NT.Storix.Service
```

---

## Usage

1. **Jobs → New**: give the job a name.
2. **Schedule**: Manual, Daily, Weekly or a cron expression (`minute hour day month weekday`), with an optional time zone. The editor shows the next three run times.
3. **Source**
   - **Files**: files and folders, one per line, plus exclude patterns (`*.tmp`, `node_modules`…).
   - **SQL Server**: a connection string and databases. **Load databases** lists them from the server. The `.bak` file is written by the SQL Server engine, so when the server is remote, set a **backup directory** (UNC path) that both SQL Server and Storix can reach.
   - **MongoDB**: a connection string, an optional database and optional `--oplog`.
4. **Processing**: compression (ZIP levels or zstd), AES-256 encryption and verification.
5. **Destinations**: add one or more. **Test connection** checks the settings.
6. **Retention & Retry**, then **Notifications** (e-mail; set up SMTP in **Settings**).
7. Save. Use **Run now** to start a backup right away and follow it in **History**.

The **Jobs** tab shows a one-line summary (how many jobs are OK, need attention or are running, and what runs next), a colored status per job with a live run time, and friendly times ("in 3 h", "Yesterday 21:15"). Actions are enabled only when they apply; right-click a job for all of them. Shortcuts: **Ctrl+N** new job, **Enter** edit, **Ctrl+R** run now, **Ctrl+D** duplicate, **Del** delete, **Ctrl+F** search, **F5** refresh. Routine confirmations appear in the status bar instead of dialogs, and the window, tab and column widths are remembered.

Every destination type has a **setup guide** next to its settings: where to create the credentials (Google Cloud, Dropbox App Console, Azure app registrations, S3 keys, Telegram bot…), where to sign in, and the pitfalls, with direct links.

The **Dashboard** tab shows the success rate of the last 30 days, the space each job and each destination takes (estimated from the run history and the retention policy), the daily growth of the backup size and a 30-day forecast.

Storix Manager is available in **English and Persian** (right-to-left layout) with a **light, dark or system** theme: **Settings → Appearance** (per Windows user; restart the manager to apply). By default the language follows Windows. Some longer help texts and messages are still English only; translations are welcome.

While Storix Manager is open, a tray icon shows the overall status (OK, running, failed), offers **Run now** for every job and pops up a notice when a backup fails. Minimizing the window hides it to the tray. Start the manager with `Storix.Manager.exe --tray` to open it straight into the tray, for example from a scheduled task at log-on (the manager needs administrator rights, so the classic *Run* registry key does not work).

### Backup files

Each run produces:

```
<job-name>_<yyyyMMdd_HHmmss>.zip          (or .zip.aes when encrypted)
<job-name>_<yyyyMMdd_HHmmss>.zip.sha256
```

With **Zstd** or **ZstdSmallest** compression the archive is `.zip.zst` (`.zip.zst.aes` when encrypted): a normal ZIP with stored entries, wrapped in a single Zstandard frame. Zstd (level 3) is usually as small as ZIP *Optimal* and several times faster; ZstdSmallest (level 19) is slower to create but gives the smallest files. Without Storix, run `zstd -d file.zip.zst` and open the ZIP with any tool. Older `.zip` backups keep working.

Every backup also gets a `<name>.index` file: a compressed list of the files it contains (encrypted like the archive). The manager uses it to browse, search and restore single files.

When splitting is enabled, the archive is stored as `<name>.partNNNN` volumes plus `<name>.manifest.json`. The manifest lists every volume with its SHA-256 and is uploaded last. To restore from a folder, point the restore wizard at the manifest or any volume.

Inside the ZIP: `files and folders`, `sqlserver/<db>.bak` or `mongodb/mongodb_<db>.archive`.

### Restoring

In the manager, open **Tools → Restore backup…** (or click **Restore…** on the Jobs tab):

1. Choose a job and a destination, click **Load backups** and pick one. Or choose a backup file on disk.
   Click **Browse files…** to search inside the backup and pick single files or folders to restore. Only a small encrypted index file is downloaded for this.
2. Choose an empty target folder and enter the encryption password if the backup is encrypted.
3. Click **Restore**. Storix downloads the backup, verifies the SHA-256 checksum, decrypts it and extracts it.
4. If the backup contains databases, click **Restore database…**:
   - SQL Server: `RESTORE DATABASE ... WITH MOVE` under a new name (or replacing the existing one).
   - MongoDB: `mongorestore`, optionally into a different database.

Without the manager:

1. Check the file: `sha256sum -c file.zip.aes.sha256` (or `Get-FileHash` in PowerShell).
2. Decrypt `.zip.aes` files with **Tools → Decrypt backup file…**.
3. For `.zip.zst` files, run `zstd -d` first. Extract the ZIP, then use `RESTORE DATABASE` or `mongorestore --archive=...`.

The encrypted file format is documented in [`AesFileEncryptor.cs`](src/NT.Storix.Core/Processing/AesFileEncryptor.cs):

- Version 1 (password): `"STRX" | 1 | iterations | salt | IV | AES-256-CBC ciphertext | HMAC-SHA256`
- Version 2 (public key): `"STRX" | 2 | key id | RSA-OAEP-wrapped data key | IV | AES-256-CBC ciphertext | HMAC-SHA256`

**Public-key mode (ransomware protection):** generate a key pair in the job editor (**Processing → Generate key pair…**) or with `storix keygen --out private.pem --passphrase …`. The server keeps only the public key, and you store the private key file offline. Even an attacker who takes over the server cannot decrypt the old backups. To restore, select the private key file and enter its passphrase.

> **Keep your encryption passwords safe.** Without the password (and the key file, if you use one), an encrypted backup cannot be restored. In the job editor, **Processing → Recovery sheet…** prints everything needed for a restore. You can also add a random **key file**, which is combined with the password.

### Command line

`storix.exe` is installed next to the manager. The release page also offers `storix-<version>-x64.exe`, a single self-contained file for disaster recovery. With only that file, the backup and the password, you can verify, list and restore backups on any Windows machine:

```powershell
storix verify   web_20260101_010000.zip.aes --password-env STORIX_PW
storix list-files web_20260101_010000.zip.aes --password-env STORIX_PW
storix restore  web_20260101_010000.zip.aes --to D:\restore --include "wwwroot/web.config" --password-env STORIX_PW
```

Job commands use the Storix database of the machine:

```powershell
storix jobs
storix run "SQL Server nightly" --wait        # queue and wait for the result
storix run "SQL Server nightly" --dry-run     # what would happen, nothing is written
storix history --limit 50
storix cancel "SQL Server nightly"
```

### Config as code

Job files (the export format) can be kept in version control. Write secrets as `${env:NAME}` placeholders:

```json
"processing": { "encrypt": true, "encryptionPassword": "${env:STORIX_ARCHIVE_PASSWORD}" }
```

```powershell
storix validate jobs.storix.json    # checks every job, reports missing variables
storix apply    jobs.storix.json    # imports the jobs, resolving the placeholders
```

The manager also offers **New from template** (SQL Server nightly to S3, log backups, website to SFTP, documents to NAS, MongoDB to Google Drive) and a **Dry run** button.

### Import / export

**File → Export configuration** saves every job (and the global settings) to a `.storix.json` file:

- **Without secrets**: passwords, connection strings and encryption keys are removed.
- **With secrets**: they are kept, encrypted with a passphrase you choose.

**File → Import configuration** loads the file. A job with the same id is replaced.

---

### Local API

The service listens on a local named pipe (`\\.\pipe\Storix`; a Unix socket on Linux) that only administrators and SYSTEM (Linux: root) can open. The manager, the tray icon and the CLI use it to start, cancel and drill jobs immediately and to read what is running (`storix status`). Requests are still recorded in the database, so when the service is not reachable they are simply picked up at its next check.

### Updates

Storix Manager checks the GitHub releases once a day (turn it off, or opt in to pre-releases, under **Settings → Updates**; **Help → Check for updates** checks now). **Download and install** downloads the MSI, checks it against the release's `SHA256SUMS.txt` and verifies its Authenticode signature: an invalid signature, or a signer other than the one of the installed version, is refused. Unsigned releases are only installed after you confirm. The service never updates itself.

### Metrics and tracing

Metrics and tracing are configured under **Settings → Monitoring** in Storix Manager, or in the `observability` section of an exported configuration:

```json
"observability": {
  "metricsEnabled": true,
  "metricsPort": 9464,
  "metricsRemoteAccess": false,
  "otlpEndpoint": "http://localhost:4317"
}
```

With metrics enabled the service serves Prometheus text format at `http://localhost:9464/metrics`. Remote access needs a URL ACL (`netsh http add urlacl url=http://+:9464/ user=...`) when the service does not run as LocalSystem. Warnings and errors are also written to the Windows Event Log under the source `Storix`. When `otlpEndpoint` is set, each backup run and upload is exported as an OpenTelemetry trace (activity source `NT.Storix`); restart the service after changing it.

## Security notes

- Secrets in `storix.db` are encrypted with Windows DPAPI in machine scope, so the database file is useless on another computer. Anyone with administrator rights on the machine can still read them. Keep the machine secure. On Linux they are encrypted with a root-only machine key instead (see *Linux agent*).
- `%ProgramData%\Storix` should be writable by administrators only.
- **Service → Run as account…** runs the service as Network Service, a dedicated user or a group Managed Service Account (gMSA). Storix grants only *Log on as a service*, Modify on its data folder and, optionally, Backup Operators membership.
- **Settings → Ask for my Windows password…** requires Windows re-authentication before restores, deletions, exports with secrets and service changes. Failed confirmations are written to the audit log.
- For FTP, prefer **FTPS**, or better, **SFTP**. Enable *Accept any certificate* only for trusted self-signed servers. For SFTP, set the host key fingerprint.
- **Dropbox / OneDrive** use OAuth with PKCE: register your own app (Dropbox App Console / Azure app registration, public client) with the redirect URI `http://localhost:53682/`, enter its key/client ID and click **Sign in**. Refresh tokens are stored encrypted.
- **Google Drive** supports two sign-in modes:
  - **User account** (personal My Drive): create an OAuth client ID of type *Desktop app* in the Google Cloud console (with the Drive API enabled), enter the client ID and secret in the destination, and click **Sign in with Google**. The refresh token is stored encrypted.
  - **Service account**: service accounts have no storage quota of their own, so use a folder inside a **Shared Drive** and add the service account as a member.

---

## Feedback & contact

Storix is a community project, and your feedback shapes what comes next.

- **Bugs and feature requests:** [open an issue on GitHub](https://github.com/nimatalebi/Storix/issues/new)
- **Questions and private feedback:** [nimatweb@gmail.com](mailto:nimatweb@gmail.com)
- **In the app:** open **Help → Send feedback…**. It creates a GitHub issue or an e-mail with the Storix version and Windows version filled in. You review it before anything is sent.

Please never include passwords, connection strings or encryption keys in an issue.

## About

Storix is built by the **Storix Contributors** and developed openly at
**[github.com/nimatalebi/Storix](https://github.com/nimatalebi/Storix)**.
The goal is a simple, dependable, fully open-source backup agent for Windows servers and workstations.
Open **Help → About Storix** in the manager to see the version, the license and the project links.

See the [roadmap](docs/ROADMAP.md) for what is planned next.

## Contributing

Contributions are welcome: bug reports, ideas and pull requests. Read [CONTRIBUTING.md](CONTRIBUTING.md) first.

1. Fork the repository and create a branch.
2. Keep the code style (see `.editorconfig`) and add tests for new behavior.
3. Make sure `dotnet build` and `dotnet test` pass.
4. Open a pull request that explains the change.

To add a new destination (for example S3), implement `IBackupDestination`, add its options to `DestinationDefinition` and register it in `DestinationFactory`. To add a source, do the same with `IBackupSource` and `SourceFactory`.

## License

Storix is released under the [MIT License](LICENSE) by the Storix Contributors. You are free to use, copy, modify and distribute it, including commercially.
