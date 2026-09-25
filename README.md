# Storix

**Storix** is an open-source backup agent for Windows.
A Windows service runs your backup jobs on a schedule. A Windows Forms app (**Storix Manager**) lets you set up jobs, see backup history and control the service.

Storix backs up **files and folders**, **SQL Server** databases (`.bak`) and **MongoDB** (`mongodump`). It can compress and encrypt each backup, then send it to a **local/UNC folder**, **FTP/FTPS**, **SFTP** or **Google Drive**. It also retries failed steps, resumes interrupted uploads, verifies every backup, deletes old backups by your retention rules and keeps a full history in SQLite.

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
│   └── MongoDB
│       └── mongodump
│
├── Processing
│   ├── Compression
│   ├── Encryption
│   ├── Chunking            (planned)
│   └── Retention
│
├── Destinations
│   ├── Local Folder
│   ├── FTP
│   ├── SFTP
│   ├── Google Drive
│   └── S3                  (planned)
│
└── Monitoring
    ├── Logs
    ├── Backup History
    ├── Success/Failure
    └── Notifications
```

| Capability | How Storix handles it |
|---|---|
| **Resume upload** | Uploads go to `<name>.partial` first. On retry, FTP and SFTP (and local folders) continue from the bytes already sent. Google Drive uses its resumable upload protocol. |
| **Retry** | The database dump and each upload are retried with exponential back-off (attempt count, first delay and multiplier are set per job). |
| **Checksum** | A SHA-256 hash is saved in the history and uploaded as a `sha256sum`-compatible `.sha256` file next to each archive. |
| **Encryption** | AES-256-CBC with HMAC-SHA256 (encrypt-then-MAC). The key comes from your password via PBKDF2-SHA256 with 600,000 iterations. Encryption streams data, so large files are fine. |
| **Retention** | "Keep last N" and/or "delete older than N days", applied on every destination. The newest backup is never deleted. |
| **Concurrent jobs** | Jobs run in parallel up to a global limit. The same job never runs twice at once. |
| **Large files** | Everything is streamed. ZIP64 is supported. No step loads a whole file into memory. |
| **Crash recovery** | At startup, runs left in progress are marked *Interrupted* and leftover temporary files are removed. The service is set to restart automatically if it fails. |
| **Partial upload cleanup** | Leftover `.partial` files from interrupted runs are deleted from destinations. |
| **Database consistency** | SQL Server: `BACKUP DATABASE … WITH COPY_ONLY, CHECKSUM`, so your existing backup chain is left intact. MongoDB: optional `--oplog` for a point-in-time dump of a replica set. |
| **Backup verification** | Optional `RESTORE VERIFYONLY`. Every archive can be re-read (and decrypted) before upload. The size of each uploaded file is checked. |
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
| `src/NT.Storix.Service` | Worker Service host that runs as the Windows service (`Storix.Service.exe`). Logs with Serilog to `%ProgramData%\Storix\logs`. |
| `src/NT.Storix.WinForms` | Storix Manager (`Storix.Manager.exe`), the desktop UI. |
| `tests/NT.Storix.Core.Tests` | xUnit tests: encryption, archives, schedules, retention, persistence, import/export, restore and the full pipeline. |
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

These tests start real SFTP, FTP, SQL Server and MongoDB (replica set) servers in Docker. For each one they back up, restore and compare the data. They are opt-in:

```bash
STORIX_INTEGRATION_TESTS=1 dotnet test tests/NT.Storix.IntegrationTests
```

### Publish and install

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
4. **Processing**: compression level, AES-256 encryption and verification.
5. **Destinations**: add one or more. **Test connection** checks the settings.
6. **Retention & Retry**, then **Notifications** (e-mail; set up SMTP in **Settings**).
7. Save. Use **Run now** to start a backup right away and follow it in **History**.

### Backup files

Each run produces:

```
<job-name>_<yyyyMMdd_HHmmss>.zip          (or .zip.aes when encrypted)
<job-name>_<yyyyMMdd_HHmmss>.zip.sha256
```

Inside the ZIP: `files and folders`, `sqlserver/<db>.bak` or `mongodb/mongodb_<db>.archive`.

### Restoring

1. Check the file: `sha256sum -c file.zip.aes.sha256` (or `Get-FileHash` in PowerShell).
2. If the file is encrypted: in the manager, open **Tools → Decrypt backup file…** to turn the `.zip.aes` file back into a `.zip`.
3. Extract the ZIP, then:
   - SQL Server: `RESTORE DATABASE [name] FROM DISK = 'path\to\db.bak' WITH ...`
   - MongoDB: `mongorestore --archive=mongodb_<db>.archive`

The encrypted file format is documented in [`AesFileEncryptor.cs`](src/NT.Storix.Core/Processing/AesFileEncryptor.cs):
`"STRX" | version | iterations | salt | IV | AES-256-CBC ciphertext | HMAC-SHA256`.

> **Keep your encryption passwords safe.** Without the password, an encrypted backup cannot be restored.

### Import / export

**File → Export configuration** saves every job (and the global settings) to a `.storix.json` file:

- **Without secrets**: passwords, connection strings and encryption keys are removed.
- **With secrets**: they are kept, encrypted with a passphrase you choose.

**File → Import configuration** loads the file. A job with the same id is replaced.

---

## Security notes

- Secrets in `storix.db` are encrypted with Windows DPAPI in machine scope, so the database file is useless on another computer. Anyone with administrator rights on the machine can still read them. Keep the machine secure.
- `%ProgramData%\Storix` should be writable by administrators only.
- For FTP, prefer **FTPS**, or better, **SFTP**. Enable *Accept any certificate* only for trusted self-signed servers. For SFTP, set the host key fingerprint.
- **Google Drive** uses a service account. Service accounts have no storage quota of their own, so use a folder inside a **Shared Drive** and add the service account as a member.

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
