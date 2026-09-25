using NT.Storix.Core.Models;

namespace NT.Storix.Core.Destinations;

/// <summary>A text in English and Persian.</summary>
public sealed record GuideText(string En, string Fa)
{
    public string For(bool persian) => persian ? Fa : En;
}

public sealed record GuideLink(GuideText Label, string Url);

/// <param name="Title">What the user is about to set up.</param>
/// <param name="Steps">Numbered steps: where each value comes from and where to sign in.</param>
/// <param name="Links">Pages the steps refer to (opened in the browser).</param>
/// <param name="Note">Pitfalls worth knowing (optional).</param>
public sealed record DestinationGuide(GuideText Title, IReadOnlyList<GuideText> Steps, IReadOnlyList<GuideLink> Links, GuideText? Note = null);

/// <summary>Step-by-step setup instructions shown next to the destination settings.</summary>
public static class DestinationGuides
{
    private static GuideText T(string en, string fa) => new(en, fa);

    private static GuideLink L(string en, string fa, string url) => new(new GuideText(en, fa), url);

    /// <summary>The guide for a destination (Google Drive depends on the chosen sign-in mode).</summary>
    public static DestinationGuide For(DestinationDefinition destination) => destination.Kind switch
    {
        DestinationKind.GoogleDrive => destination.GoogleDrive.AuthMode == GoogleDriveAuthMode.UserAccount ? GoogleDriveUser : GoogleDriveServiceAccount,
        _ => For(destination.Kind),
    };

    public static DestinationGuide For(DestinationKind kind) => kind switch
    {
        DestinationKind.LocalFolder => LocalFolder,
        DestinationKind.Ftp => Ftp,
        DestinationKind.Sftp => Sftp,
        DestinationKind.GoogleDrive => GoogleDriveUser,
        DestinationKind.S3 => S3,
        DestinationKind.AzureBlob => AzureBlob,
        DestinationKind.WebDav => WebDav,
        DestinationKind.Dropbox => Dropbox,
        DestinationKind.OneDrive => OneDrive,
        DestinationKind.Rclone => Rclone,
        DestinationKind.Telegram => Telegram,
        DestinationKind.Plugin => Plugin,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static readonly DestinationGuide LocalFolder = new(
        T("Local folder, NAS or network share", "پوشهٔ محلی، NAS یا اشتراک شبکه"),
        [
            T("Path: a folder on another disk (e.g. D:\\Backups) or a network share written as \\\\server\\share\\folder.",
              "Path: پوشه‌ای روی دیسکی دیگر (مثلاً D:\\Backups) یا اشتراک شبکه به شکل \\\\server\\share\\folder."),
            T("Do not use mapped drive letters (Z:): the Storix service does not see them. Always use the \\\\server\\share form.",
              "از درایوهای Map‌شده (مثل Z:) استفاده نکنید؛ سرویس استوریکس آن‌ها را نمی‌بیند. همیشه شکل \\\\server\\share را بنویسید."),
            T("NAS or another computer: create a user for backups on the NAS (Synology: Control Panel → User; QNAP: Users) with write access to the shared folder.",
              "NAS یا رایانهٔ دیگر: روی NAS یک کاربر مخصوص پشتیبان بسازید (Synology: Control Panel ← User؛ QNAP: Users) با دسترسی نوشتن روی پوشهٔ اشتراکی."),
            T("UserName / Password: that NAS user, written as NAS\\user or DOMAIN\\user. Leave empty if the service account already has access.",
              "UserName / Password: همان کاربر NAS به شکل NAS\\user یا DOMAIN\\user. اگر حساب سرویس خودش دسترسی دارد خالی بگذارید."),
            T("Click Test connection.", "روی «آزمایش اتصال» بزنید."),
        ],
        [],
        T("A disk in the same computer does not protect against theft, fire or ransomware: add an off-site destination too.",
          "دیسکی در همان رایانه در برابر سرقت، آتش‌سوزی یا باج‌افزار محافظت نمی‌کند؛ یک مقصد خارج از محل هم اضافه کنید."));

    private static readonly DestinationGuide Ftp = new(
        T("FTP / FTPS server", "سرور FTP / FTPS"),
        [
            T("Host, Port, UserName and Password come from your hosting control panel (cPanel: Files → FTP Accounts; DirectAdmin: FTP Management) or from the NAS/server administrator.",
              "Host، Port، UserName و Password را از کنترل‌پنل هاست (cPanel: Files ← FTP Accounts؛ DirectAdmin: FTP Management) یا از مدیر سرور/NAS بگیرید."),
            T("Create a dedicated FTP account whose home folder is only the backup folder.",
              "یک حساب FTP جداگانه بسازید که پوشهٔ خانه‌اش فقط پوشهٔ پشتیبان باشد."),
            T("Encryption: keep Explicit (FTPS) unless the server supports plain FTP only. Turn on AcceptAnyCertificate only for a self-signed server you trust.",
              "Encryption: روی Explicit (FTPS) بماند مگر سرور فقط FTP ساده داشته باشد. AcceptAnyCertificate را فقط برای سرور مطمئن با گواهی خودامضا روشن کنید."),
            T("RemotePath: the folder on the server, e.g. /backups/server1. Click Test connection.",
              "RemotePath: پوشه روی سرور، مثلاً ‎/backups/server1. سپس «آزمایش اتصال»."),
        ],
        [],
        T("Prefer SFTP when the server offers it: it is encrypted and simpler through firewalls.",
          "اگر سرور SFTP دارد آن را ترجیح دهید: رمزنگاری‌شده است و با فایروال راحت‌تر کار می‌کند."));

    private static readonly DestinationGuide Sftp = new(
        T("SFTP (SSH) server", "سرور SFTP (SSH)"),
        [
            T("Host and Port (usually 22): the Linux server, VPS or NAS (Synology: Control Panel → File Services → FTP → enable SFTP).",
              "Host و Port (معمولاً ۲۲): سرور لینوکس، VPS یا NAS (Synology: Control Panel ← File Services ← FTP ← فعال‌کردن SFTP)."),
            T("Create a user for backups on the server (Linux: sudo adduser backup) and a folder it owns, e.g. /home/backup/server1.",
              "روی سرور یک کاربر برای پشتیبان بسازید (لینوکس: sudo adduser backup) و پوشه‌ای متعلق به او، مثلاً ‎/home/backup/server1."),
            T("Password, or better a key: run ssh-keygen -t ed25519 on this computer, add the .pub line to ~/.ssh/authorized_keys of that user and set PrivateKeyPath to the private key file.",
              "رمز عبور، یا بهتر کلید: روی این رایانه ssh-keygen -t ed25519 را اجرا کنید، خط فایل ‎.pub را به ‎~/.ssh/authorized_keys آن کاربر اضافه و PrivateKeyPath را فایل کلید خصوصی بگذارید."),
            T("HostKeyFingerprint (recommended): on the server run ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub and paste the SHA256:... value. Storix then refuses a different server.",
              "HostKeyFingerprint (توصیه‌شده): روی سرور ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub را اجرا و مقدار SHA256:... را اینجا بگذارید تا استوریکس به سرور دیگری وصل نشود."),
            T("RemotePath: the backup folder. Click Test connection.", "RemotePath: پوشهٔ پشتیبان. سپس «آزمایش اتصال»."),
        ],
        []);

    private static readonly DestinationGuide GoogleDriveUser = new(
        T("Google Drive with your Google account (My Drive)", "گوگل‌درایو با حساب گوگل شما (My Drive)"),
        [
            T("Open the Google Cloud console and sign in with any Google account. Top bar → project list → New project (e.g. \"Storix\") → Create.",
              "کنسول Google Cloud را باز کنید و با یک حساب گوگل وارد شوید. نوار بالا ← فهرست پروژه‌ها ← New project (مثلاً «Storix») ← Create."),
            T("APIs & Services → Library → search \"Google Drive API\" → Enable.",
              "APIs & Services ← Library ← «Google Drive API» را جست‌وجو و Enable کنید."),
            T("APIs & Services → OAuth consent screen (Google Auth Platform): Get started → app name \"Storix\", your e-mail → Audience: External → Create.",
              "APIs & Services ← OAuth consent screen (Google Auth Platform): روی Get started بزنید ← نام برنامه «Storix» و ایمیل خودتان ← Audience: External ← Create."),
            T("Audience → Publish app (status \"In production\"). Important: in \"Testing\" status Google cancels the sign-in after 7 days and backups stop. Verification is not needed for your own use.",
              "Audience ← Publish app (وضعیت «In production»). مهم: در وضعیت «Testing» گوگل ورود را بعد از ۷ روز باطل می‌کند و پشتیبان‌گیری متوقف می‌شود. برای استفادهٔ شخصی نیازی به Verification نیست."),
            T("Clients (or Credentials) → Create client → Application type \"Desktop app\" → Create. Copy the Client ID into OAuthClientId and the Client secret into OAuthClientSecret.",
              "Clients (یا Credentials) ← Create client ← Application type: «Desktop app» ← Create. مقدار Client ID را در OAuthClientId و Client secret را در OAuthClientSecret بگذارید."),
            T("Set AuthMode to UserAccount and click \"Sign in with Google\". In the browser choose the account that should store the backups. If Google shows \"Google hasn't verified this app\", click Advanced → Go to Storix (it is your own app), then Continue.",
              "AuthMode را UserAccount بگذارید و «ورود با گوگل» را بزنید. در مرورگر حسابی را انتخاب کنید که پشتیبان‌ها باید در آن ذخیره شوند. اگر پیام «Google hasn't verified this app» آمد، روی Advanced ← Go to Storix بزنید (برنامهٔ خود شماست) و Continue."),
            T("FolderId: open the folder at drive.google.com; the id is the part of the address after /folders/. Empty = the root of My Drive. Click Test connection.",
              "FolderId: پوشه را در drive.google.com باز کنید؛ شناسه بخش بعد از ‎/folders/ در نشانی است. خالی = ریشهٔ My Drive. سپس «آزمایش اتصال»."),
        ],
        [
            L("Google Cloud console", "کنسول Google Cloud", "https://console.cloud.google.com/projectcreate"),
            L("Enable the Drive API", "فعال‌کردن Drive API", "https://console.cloud.google.com/apis/library/drive.googleapis.com"),
            L("OAuth consent / clients", "صفحهٔ OAuth و کلاینت‌ها", "https://console.cloud.google.com/auth/overview"),
            L("Google Drive", "گوگل‌درایو", "https://drive.google.com"),
        ],
        T("The sign-in opens a page on http://localhost:53682 on this computer; do it on the server itself (or in a remote desktop session).",
          "ورود، صفحه‌ای روی http://localhost:53682 همین رایانه باز می‌کند؛ این کار را روی خود سرور (یا در Remote Desktop) انجام دهید."));

    private static readonly DestinationGuide GoogleDriveServiceAccount = new(
        T("Google Drive with a service account (Google Workspace shared drive)", "گوگل‌درایو با Service Account (درایو اشتراکی Google Workspace)"),
        [
            T("This mode needs Google Workspace: service accounts have no storage of their own, so backups go to a shared drive. With a personal Gmail account use AuthMode UserAccount instead.",
              "این حالت به Google Workspace نیاز دارد: Service Account فضای ذخیره‌سازی ندارد و پشتیبان‌ها در یک Shared drive ذخیره می‌شوند. با Gmail شخصی، AuthMode را UserAccount بگذارید."),
            T("Google Cloud console → create or pick a project → APIs & Services → Library → enable \"Google Drive API\".",
              "کنسول Google Cloud ← ساخت یا انتخاب پروژه ← APIs & Services ← Library ← فعال‌کردن «Google Drive API»."),
            T("IAM & Admin → Service accounts → Create service account (no roles needed) → open it → Keys → Add key → Create new key → JSON. A .json file is downloaded.",
              "IAM & Admin ← Service accounts ← Create service account (نقش لازم نیست) ← باز کردن آن ← Keys ← Add key ← Create new key ← JSON. یک فایل ‎.json دانلود می‌شود."),
            T("Copy the key to the server, e.g. C:\\ProgramData\\Storix\\keys\\drive.json, and set ServiceAccountKeyPath to it (use \"Choose key file...\"). Keep it private: it grants access to the backups.",
              "کلید را روی سرور کپی کنید، مثلاً C:\\ProgramData\\Storix\\keys\\drive.json، و ServiceAccountKeyPath را روی آن بگذارید (دکمهٔ «انتخاب فایل کلید...»). آن را محرمانه نگه دارید."),
            T("drive.google.com → Shared drives → New → open it → Manage members → add the service account e-mail (…@…iam.gserviceaccount.com) as Content manager.",
              "drive.google.com ← Shared drives ← New ← باز کردن ← Manage members ← ایمیل Service Account (…@…iam.gserviceaccount.com) را با نقش Content manager اضافه کنید."),
            T("FolderId: open the shared drive (or a folder in it) and copy the part of the address after /folders/. Click Test connection.",
              "FolderId: درایو اشتراکی (یا پوشه‌ای در آن) را باز و بخش بعد از ‎/folders/ نشانی را کپی کنید. سپس «آزمایش اتصال»."),
        ],
        [
            L("Google Cloud console", "کنسول Google Cloud", "https://console.cloud.google.com/"),
            L("Service accounts", "Service Accountها", "https://console.cloud.google.com/iam-admin/serviceaccounts"),
            L("Enable the Drive API", "فعال‌کردن Drive API", "https://console.cloud.google.com/apis/library/drive.googleapis.com"),
            L("Google Drive", "گوگل‌درایو", "https://drive.google.com"),
        ]);

    private static readonly DestinationGuide S3 = new(
        T("Amazon S3 or S3-compatible storage", "Amazon S3 یا فضای سازگار با S3"),
        [
            T("First pick your provider in \"S3 provider\": it fills in the endpoint, region and path style.",
              "ابتدا ارائه‌دهنده را در «ارائه‌دهندهٔ S3» انتخاب کنید تا Endpoint، Region و Path style پر شوند."),
            T("Create a bucket in the provider's console (private, no public access). Put its name in BucketName.",
              "در کنسول ارائه‌دهنده یک Bucket خصوصی (بدون دسترسی عمومی) بسازید و نامش را در BucketName بگذارید."),
            T("Amazon S3: IAM → Users → Create user → attach a policy that allows s3:ListBucket, s3:GetObject, s3:PutObject, s3:DeleteObject and s3:AbortMultipartUpload on this bucket only → Security credentials → Create access key.",
              "Amazon S3: IAM ← Users ← Create user ← یک Policy با مجوزهای s3:ListBucket، s3:GetObject، s3:PutObject، s3:DeleteObject و s3:AbortMultipartUpload فقط برای همین Bucket ← Security credentials ← Create access key."),
            T("Cloudflare R2: R2 → Manage API tokens → Create API token → Object Read & Write, limited to the bucket. The endpoint contains your account id.",
              "Cloudflare R2: بخش R2 ← Manage API tokens ← Create API token ← Object Read & Write محدود به همان Bucket. Endpoint شامل Account ID شماست."),
            T("Wasabi / Backblaze B2 / ArvanCloud / MinIO: create an access key (application key) in their panel with access to the bucket.",
              "Wasabi / Backblaze B2 / ابر آروان / MinIO: در پنل آن‌ها یک Access key (Application key) با دسترسی به Bucket بسازید."),
            T("Copy the Access key ID and the Secret access key into Storix (the secret is shown only once). Click Test connection.",
              "Access key ID و Secret access key را در استوریکس بگذارید (Secret فقط یک بار نشان داده می‌شود). سپس «آزمایش اتصال»."),
            T("Optional: Object Lock (immutable backups) must be enabled when the bucket is created.",
              "اختیاری: Object Lock (پشتیبان تغییرناپذیر) باید هنگام ساخت Bucket فعال شود."),
        ],
        [
            L("AWS IAM users", "کاربران AWS IAM", "https://console.aws.amazon.com/iam/home#/users"),
            L("AWS S3 buckets", "Bucketهای AWS S3", "https://s3.console.aws.amazon.com/s3/buckets"),
            L("Cloudflare R2", "Cloudflare R2", "https://dash.cloudflare.com/?to=/:account/r2/overview"),
            L("Backblaze B2 keys", "کلیدهای Backblaze B2", "https://secure.backblaze.com/app_keys.htm"),
            L("Wasabi console", "کنسول Wasabi", "https://console.wasabisys.com/"),
        ]);

    private static readonly DestinationGuide AzureBlob = new(
        T("Azure Blob Storage", "Azure Blob Storage"),
        [
            T("Azure portal → Storage accounts → Create (Standard, LRS or GRS as you like).",
              "پرتال Azure ← Storage accounts ← Create (Standard، LRS یا GRS به دلخواه)."),
            T("In the storage account: Data storage → Containers → + Container, name it (e.g. backups), access level Private. Put the name in Container.",
              "داخل Storage account: Data storage ← Containers ← ‎+ Container، نام (مثلاً backups) با سطح دسترسی Private. همین نام را در Container بگذارید."),
            T("Security + networking → Access keys → Show → copy the Connection string of key1 into ConnectionString.",
              "Security + networking ← Access keys ← Show ← مقدار Connection string کلید key1 را در ConnectionString بگذارید."),
            T("Optional: AccessTier Cool or Cold is cheaper for backups that are rarely read. Click Test connection.",
              "اختیاری: AccessTier روی Cool یا Cold برای پشتیبان‌هایی که کم خوانده می‌شوند ارزان‌تر است. سپس «آزمایش اتصال»."),
        ],
        [L("Azure storage accounts", "Storage accountهای Azure", "https://portal.azure.com/#browse/Microsoft.Storage%2FStorageAccounts")]);

    private static readonly DestinationGuide WebDav = new(
        T("WebDAV (Nextcloud, ownCloud, NAS)", "WebDAV (نکست‌کلاد، ownCloud، NAS)"),
        [
            T("Nextcloud: open Files → Files settings (bottom left) → copy the WebDAV address and add the folder, e.g. https://cloud.example.com/remote.php/dav/files/USER/Backups.",
              "نکست‌کلاد: Files ← Files settings (پایین سمت چپ) ← نشانی WebDAV را کپی و نام پوشه را به آن اضافه کنید، مثلاً https://cloud.example.com/remote.php/dav/files/USER/Backups."),
            T("Nextcloud password: Settings → Security → Devices & sessions → create an app password named \"Storix\" and use it instead of your normal password (needed with two-factor login).",
              "رمز نکست‌کلاد: Settings ← Security ← Devices & sessions ← یک App password به نام «Storix» بسازید و به‌جای رمز معمولی استفاده کنید (با ورود دومرحله‌ای ضروری است)."),
            T("Synology NAS: Package Center → install \"WebDAV Server\" → enable HTTPS (port 5006). URL: https://NAS-ADDRESS:5006/shared-folder.",
              "Synology: Package Center ← نصب «WebDAV Server» ← فعال‌کردن HTTPS (پورت ۵۰۰۶). نشانی: https://NAS-ADDRESS:5006/shared-folder."),
            T("UserName / Password: the account with write access. Click Test connection.",
              "UserName / Password: حسابی که دسترسی نوشتن دارد. سپس «آزمایش اتصال»."),
        ],
        [L("Nextcloud WebDAV help", "راهنمای WebDAV نکست‌کلاد", "https://docs.nextcloud.com/server/latest/user_manual/en/files/access_webdav.html")]);

    private static readonly DestinationGuide Dropbox = new(
        T("Dropbox", "دراپ‌باکس"),
        [
            T("Open the Dropbox App Console (sign in with the Dropbox account that should store the backups) → Create app → Scoped access → App folder → name it, e.g. \"Storix-Backup-yourname\".",
              "Dropbox App Console را باز کنید (با حسابی که پشتیبان‌ها باید در آن ذخیره شوند) ← Create app ← Scoped access ← App folder ← نامی مثل «Storix-Backup-yourname»."),
            T("Permissions tab: tick files.metadata.read, files.content.read and files.content.write → Submit.",
              "زبانهٔ Permissions: گزینه‌های files.metadata.read، files.content.read و files.content.write را تیک بزنید ← Submit."),
            T("Settings tab: under OAuth 2 → Redirect URIs add http://localhost:53682/ → Add. Copy the App key into AppKey (no secret needed).",
              "زبانهٔ Settings: در OAuth 2 ← Redirect URIs مقدار http://localhost:53682/ را Add کنید. App key را در AppKey بگذارید (Secret لازم نیست)."),
            T("Click \"Sign in with Dropbox\" and allow access in the browser.",
              "«ورود با دراپ‌باکس» را بزنید و در مرورگر اجازه دهید."),
            T("Folder: with an App folder the path is inside Apps/<app name>, e.g. /server1. Click Test connection.",
              "Folder: در حالت App folder مسیر داخل Apps/<نام برنامه> است، مثلاً ‎/server1. سپس «آزمایش اتصال»."),
        ],
        [L("Dropbox App Console", "Dropbox App Console", "https://www.dropbox.com/developers/apps/create")],
        T("Sign in on the server itself: the browser returns to http://localhost:53682 on this computer.",
          "ورود را روی خود سرور انجام دهید: مرورگر به http://localhost:53682 همین رایانه برمی‌گردد."));

    private static readonly DestinationGuide OneDrive = new(
        T("OneDrive / SharePoint", "وان‌درایو / شیرپوینت"),
        [
            T("Open App registrations in the Azure portal (or entra.microsoft.com) → New registration → name \"Storix\".",
              "App registrations را در پرتال Azure (یا entra.microsoft.com) باز کنید ← New registration ← نام «Storix»."),
            T("Supported account types: \"Personal Microsoft accounts only\" for personal OneDrive, \"Accounts in this organizational directory\" for OneDrive for Business, or \"any directory and personal\" for both.",
              "Supported account types: برای وان‌درایو شخصی «Personal Microsoft accounts only»، برای OneDrive for Business «Accounts in this organizational directory»، یا «any directory and personal» برای هر دو."),
            T("Redirect URI: platform \"Public client/native (mobile & desktop)\", value http://localhost:53682/ → Register.",
              "Redirect URI: پلتفرم «Public client/native (mobile & desktop)» با مقدار http://localhost:53682/ ← Register."),
            T("Authentication → Advanced settings → Allow public client flows: Yes → Save. API permissions → Add → Microsoft Graph → Delegated: Files.ReadWrite, offline_access, User.Read.",
              "Authentication ← Advanced settings ← Allow public client flows: Yes ← Save. API permissions ← Add ← Microsoft Graph ← Delegated: Files.ReadWrite، offline_access، User.Read."),
            T("Overview: copy the Application (client) ID into ClientId. Tenant: consumers (personal), organizations or your tenant id (work), common (both).",
              "Overview: مقدار Application (client) ID را در ClientId بگذارید. Tenant: برای شخصی consumers، برای سازمانی organizations یا شناسهٔ Tenant، و برای هر دو common."),
            T("Click \"Sign in with Microsoft\", then set Folder (e.g. Backups/server1) and click Test connection.",
              "«ورود با مایکروسافت» را بزنید، سپس Folder (مثلاً Backups/server1) را تنظیم و «آزمایش اتصال» را بزنید."),
        ],
        [
            L("App registrations", "App registrations", "https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade"),
            L("Microsoft Entra admin center", "Microsoft Entra", "https://entra.microsoft.com/"),
        ]);

    private static readonly DestinationGuide Rclone = new(
        T("rclone (40+ cloud providers)", "rclone (بیش از ۴۰ سرویس ابری)"),
        [
            T("Download rclone for Windows, unzip it, e.g. to C:\\Program Files\\rclone, and set RclonePath to rclone.exe.",
              "rclone ویندوز را دانلود و مثلاً در C:\\Program Files\\rclone باز کنید و RclonePath را روی rclone.exe بگذارید."),
            T("Create the configuration in a place the service can read: open a command prompt as administrator and run rclone config --config C:\\ProgramData\\Storix\\rclone.conf, then follow the wizard (n = new remote).",
              "پیکربندی را جایی بسازید که سرویس بخواند: خط فرمان را به‌صورت Administrator باز کنید و rclone config --config C:\\ProgramData\\Storix\\rclone.conf را اجرا و مراحل را دنبال کنید (n = remote جدید)."),
            T("ConfigPath: that rclone.conf file. Remote: the remote name, a colon and the folder, e.g. mydrive:Backups/server1.",
              "ConfigPath: همان فایل rclone.conf. Remote: نام remote، دونقطه و پوشه، مثلاً mydrive:Backups/server1."),
            T("Click Test connection.", "روی «آزمایش اتصال» بزنید."),
        ],
        [
            L("Download rclone", "دانلود rclone", "https://rclone.org/downloads/"),
            L("rclone providers", "سرویس‌های rclone", "https://rclone.org/overview/"),
        ],
        T("The service runs as a different Windows account: a config created with plain \"rclone config\" in your profile is not found. Always use --config as above.",
          "سرویس با حساب ویندوز دیگری اجرا می‌شود و پیکربندی ساخته‌شده با «rclone config» ساده در پروفایل شما پیدا نمی‌شود؛ حتماً مانند بالا از ‎--config استفاده کنید."));

    private static readonly DestinationGuide Telegram = new(
        T("Telegram or Bale channel", "کانال تلگرام یا بله"),
        [
            T("In Telegram open @BotFather → /newbot → choose a name and a username ending in \"bot\". Copy the token (123456789:AA...) into BotToken. (Bale: the Bale bot father, same steps.)",
              "در تلگرام ‎@BotFather را باز کنید ← ‎/newbot ← نام و نام‌کاربری‌ای که به «bot» ختم شود. توکن (123456789:AA...) را در BotToken بگذارید. (بله: «بات‌فادر» بله، همین مراحل.)"),
            T("Create a private channel for the backups → Administrators → Add admin → your bot, with Post, Delete and Pin messages.",
              "یک کانال خصوصی برای پشتیبان بسازید ← Administrators ← Add admin ← ربات خودتان با دسترسی ارسال، حذف و سنجاق پیام."),
            T("Chat id: post a message in the channel, then open https://api.telegram.org/bot<TOKEN>/getUpdates in a browser and copy the \"chat\":{\"id\": -100...} value into ChatId.",
              "شناسهٔ چت: در کانال یک پیام بفرستید، سپس https://api.telegram.org/bot<TOKEN>/getUpdates را در مرورگر باز کنید و مقدار ‎\"chat\":{\"id\": -100...}‎ را در ChatId بگذارید."),
            T("If this server cannot reach Telegram: deploy the Cloudflare Worker relay from tools/telegram-relay, set ApiBaseUrl to its address and RelayKey to its RELAY_KEY.",
              "اگر این سرور به تلگرام دسترسی ندارد: Worker رله را از پوشهٔ tools/telegram-relay روی Cloudflare مستقر کنید، ApiBaseUrl را نشانی آن و RelayKey را همان RELAY_KEY بگذارید."),
            T("Turn on encryption in the job (anyone in the channel can download the files). Click Test connection.",
              "رمزنگاری کار را روشن کنید (هر عضو کانال می‌تواند فایل‌ها را دانلود کند). سپس «آزمایش اتصال»."),
        ],
        [
            L("@BotFather", "@BotFather", "https://t.me/BotFather"),
            L("Cloudflare Workers", "Cloudflare Workers", "https://dash.cloudflare.com/?to=/:account/workers-and-pages/create"),
            L("Setup guide (relay)", "راهنمای کامل (رله)", "https://github.com/nimatalebi/Storix/blob/main/docs/TELEGRAM.md"),
        ],
        T("Do not unpin the \"storix-catalog.json\" message: it is how Storix finds the backups from another computer.",
          "پیام سنجاق‌شدهٔ «storix-catalog.json» را از سنجاق خارج نکنید؛ استوریکس با آن پشتیبان‌ها را از رایانهٔ دیگر پیدا می‌کند."));

    private static readonly DestinationGuide Plugin = new(
        T("Plugin destination", "مقصد افزونه"),
        [
            T("Copy the plugin DLL into the \"plugins\" folder next to Storix and restart the service.",
              "فایل DLL افزونه را در پوشهٔ «plugins» کنار استوریکس کپی و سرویس را دوباره راه‌اندازی کنید."),
            T("Plugin: its id. Settings: one name=value per line; secret settings: name=value; name2=value2 (stored encrypted).",
              "Plugin: شناسهٔ افزونه. Settings: هر خط یک name=value؛ تنظیمات محرمانه: name=value; name2=value2 (رمزنگاری‌شده ذخیره می‌شوند)."),
        ],
        [L("Plugin guide", "راهنمای افزونه‌ها", "https://github.com/nimatalebi/Storix/blob/main/docs/PLUGINS.md")]);
}

/// <summary>Step-by-step setup instructions for notification channels.</summary>
public static class ChannelGuides
{
    private static GuideText T(string en, string fa) => new(en, fa);

    private static GuideLink L(string en, string fa, string url) => new(new GuideText(en, fa), url);

    public static DestinationGuide For(NotificationChannelKind kind) => kind switch
    {
        NotificationChannelKind.Telegram => Telegram,
        NotificationChannelKind.Bale => Bale,
        NotificationChannelKind.Slack => Slack,
        NotificationChannelKind.Teams => Teams,
        NotificationChannelKind.Discord => Discord,
        _ => Webhook,
    };

    private static readonly DestinationGuide Telegram = new(
        T("Telegram notifications", "اعلان در تلگرام"),
        [
            T("Open @BotFather in Telegram → /newbot → pick a name and a username ending in \"bot\" → copy the token into BotToken.",
              "در تلگرام ‎@BotFather را باز کنید ← ‎/newbot ← نام و نام‌کاربری‌ای که به «bot» ختم شود ← توکن را در BotToken بگذارید."),
            T("Private chat: open your bot and press Start. Group or channel: add the bot (in a channel as an administrator allowed to post).",
              "چت خصوصی: ربات را باز و Start را بزنید. گروه یا کانال: ربات را اضافه کنید (در کانال به‌عنوان مدیر با اجازهٔ ارسال)."),
            T("Chat id: send any message there, open https://api.telegram.org/bot<TOKEN>/getUpdates and copy the \"chat\":{\"id\": ...} number into ChatId (channels and groups start with -100).",
              "شناسهٔ چت: یک پیام بفرستید، https://api.telegram.org/bot<TOKEN>/getUpdates را باز و عدد ‎\"chat\":{\"id\": ...}‎ را در ChatId بگذارید (کانال و گروه با ‎-100 شروع می‌شوند)."),
            T("Server without access to Telegram: set ApiBaseUrl to your Cloudflare Worker relay and RelayKey to its RELAY_KEY (see docs/TELEGRAM.md). Click Send test.",
              "سرور بدون دسترسی به تلگرام: ApiBaseUrl را نشانی Worker رله در Cloudflare و RelayKey را همان RELAY_KEY بگذارید (docs/TELEGRAM.md). سپس «ارسال آزمایشی»."),
        ],
        [
            L("@BotFather", "@BotFather", "https://t.me/BotFather"),
            L("Relay guide", "راهنمای رله", "https://github.com/nimatalebi/Storix/blob/main/docs/TELEGRAM.md"),
        ]);

    private static readonly DestinationGuide Bale = new(
        T("Bale notifications", "اعلان در بله"),
        [
            T("In Bale open the bot father (@botfather) → create a bot → copy the token into BotToken.",
              "در بله «بات‌فادر» (‎@botfather) را باز کنید ← ساخت ربات ← توکن را در BotToken بگذارید."),
            T("Open your bot and press Start (or add it to a group/channel as an administrator).",
              "ربات را باز و «شروع» را بزنید (یا آن را به‌عنوان مدیر به گروه یا کانال اضافه کنید)."),
            T("Chat id: send a message, open https://tapi.bale.ai/bot<TOKEN>/getUpdates and copy the chat id into ChatId. Click Send test.",
              "شناسهٔ چت: یک پیام بفرستید، https://tapi.bale.ai/bot<TOKEN>/getUpdates را باز و شناسهٔ چت را در ChatId بگذارید. سپس «ارسال آزمایشی»."),
        ],
        [L("Bale", "بله", "https://web.bale.ai/")]);

    private static readonly DestinationGuide Slack = new(
        T("Slack notifications", "اعلان در Slack"),
        [
            T("Open Slack apps → Create New App → From scratch → name \"Storix\" and your workspace.",
              "صفحهٔ Slack apps ← Create New App ← From scratch ← نام «Storix» و Workspace خودتان."),
            T("Incoming Webhooks → turn on → Add New Webhook to Workspace → choose the channel → Allow.",
              "Incoming Webhooks ← روشن کنید ← Add New Webhook to Workspace ← کانال را انتخاب ← Allow."),
            T("Copy the webhook URL (https://hooks.slack.com/services/...) into Url. Click Send test.",
              "نشانی Webhook (https://hooks.slack.com/services/...) را در Url بگذارید. سپس «ارسال آزمایشی»."),
        ],
        [L("Slack apps", "برنامه‌های Slack", "https://api.slack.com/apps")]);

    private static readonly DestinationGuide Teams = new(
        T("Microsoft Teams notifications", "اعلان در Microsoft Teams"),
        [
            T("In Teams, open the channel → ⋯ (More options) → Workflows.",
              "در Teams کانال را باز کنید ← ⋯ (گزینه‌های بیشتر) ← Workflows."),
            T("Choose the template \"Post to a channel when a webhook request is received\" → sign in → pick team and channel → Add workflow.",
              "الگوی «Post to a channel when a webhook request is received» را انتخاب ← ورود ← تیم و کانال ← Add workflow."),
            T("Copy the URL shown at the end into Url. Click Send test.",
              "نشانی نمایش‌داده‌شده در پایان را در Url بگذارید. سپس «ارسال آزمایشی»."),
        ],
        [L("Teams webhooks (Workflows)", "Webhook در Teams", "https://support.microsoft.com/office/create-incoming-webhooks-with-workflows-for-microsoft-teams-8ae491c7-0394-4861-ba59-055e33f75498")]);

    private static readonly DestinationGuide Discord = new(
        T("Discord notifications", "اعلان در Discord"),
        [
            T("Server Settings → Integrations → Webhooks → New Webhook.", "Server Settings ← Integrations ← Webhooks ← New Webhook."),
            T("Choose the channel and a name, then Copy Webhook URL and paste it into Url. Click Send test.",
              "کانال و نام را انتخاب کنید، سپس Copy Webhook URL و آن را در Url بگذارید. سپس «ارسال آزمایشی»."),
        ],
        [L("Discord webhooks", "Webhook در Discord", "https://support.discord.com/hc/en-us/articles/228383668")]);

    private static readonly DestinationGuide Webhook = new(
        T("Webhook (your own system)", "Webhook (سامانهٔ خودتان)"),
        [
            T("Url: an HTTPS endpoint of your system (monitoring, ticketing, n8n, Zapier...). Storix sends a JSON document with the job, status, size and message.",
              "Url: نشانی HTTPS سامانهٔ شما (مانیتورینگ، تیکتینگ، n8n، Zapier...). استوریکس یک سند JSON شامل کار، وضعیت، حجم و پیام می‌فرستد."),
            T("SigningSecret (recommended): any long random text. Storix signs the body with HMAC-SHA256 and sends X-Storix-Signature: sha256=<hex>; check it on your side.",
              "SigningSecret (توصیه‌شده): یک متن تصادفی طولانی. استوریکس بدنه را با HMAC-SHA256 امضا و X-Storix-Signature: sha256=<hex> را ارسال می‌کند؛ آن را در سمت خود بررسی کنید."),
            T("Click Send test to check that your endpoint accepts it.", "با «ارسال آزمایشی» بررسی کنید که سامانهٔ شما آن را می‌پذیرد."),
        ],
        []);
}
