namespace NT.Storix.Core.Configuration;

public sealed partial record JobTemplate
{
    /// <summary>Persian name and description, keyed by the English name.</summary>
    private static readonly Dictionary<string, (string Name, string Description)> Persian = new(StringComparer.Ordinal)
    {
        ["Website (IIS) daily to Google Drive"] = ("وب‌سایت (IIS) روزانه به گوگل‌درایو",
            "یک وب‌سایت IIS هر شب ساعت ۲ به گوگل‌درایو، رمزنگاری‌شده. لاگ، کش، فایل‌های موقت، node_modules و ‎.git کنار گذاشته می‌شوند و فایل‌های باز از Shadow Copy خوانده می‌شوند. ۷ روزانه، ۴ هفتگی و ۶ ماهانه نگهداری می‌شود."),
        ["Website weekly to SFTP"] = ("وب‌سایت هفتگی به SFTP",
            "پوشهٔ وب‌سایت هر جمعه ساعت ۲۳ به سرور SFTP، بدون لاگ و فایل‌های موقت. ۸ نسخه نگهداری می‌شود."),
        ["Busy website hourly (incremental) to NAS"] = ("وب‌سایت پرتغییر، ساعتی (افزایشی) به NAS",
            "برای سایت‌هایی با آپلود زیاد: هر ساعت فقط فایل‌های تغییرکرده و هر ۷ روز یک نسخهٔ کامل، روی اشتراک شبکه. دو هفته تاریخچهٔ ساعتی نگه داشته می‌شود."),
        ["WordPress - files"] = ("وردپرس - فایل‌ها",
            "پوشهٔ وردپرس (قالب‌ها، افزونه‌ها، آپلودها، wp-config.php) هر شب ساعت ۲ به گوگل‌درایو، بدون کش. همراه با «وردپرس - پایگاه‌داده» استفاده کنید."),
        ["WordPress - database (MySQL/MariaDB)"] = ("وردپرس - پایگاه‌داده (MySQL/MariaDB)",
            "پایگاه‌دادهٔ وردپرس هر شب ساعت ۱:۴۵ (بدون قفل‌کردن جدول‌ها) به گوگل‌درایو."),
        ["Linux web root (/var/www) to S3"] = ("پوشهٔ وب لینوکس (‎/var/www) به S3",
            "برای نسخهٔ لینوکس: ‎/var/www هر شب ساعت ۳ به فضای سازگار با S3، بدون لاگ و کش."),
        ["MongoDB database daily to Google Drive"] = ("پایگاه‌دادهٔ MongoDB روزانه به گوگل‌درایو",
            "یک پایگاه‌دادهٔ MongoDB (با mongodump) هر شب ساعت ۲:۳۰ به گوگل‌درایو، فشرده با zstd و رمزنگاری‌شده. رشتهٔ اتصال و نام پایگاه‌داده را تنظیم کنید."),
        ["MongoDB replica set (all databases, --oplog) to Google Drive"] = ("Replica set مانگو (همهٔ پایگاه‌داده‌ها با oplog) به گوگل‌درایو",
            "mongodump سازگار از همهٔ پایگاه‌داده‌ها با ‎--oplog هر شب ساعت ۲، در بخش‌های ۱ گیگابایتی."),
        ["SQL Server nightly to S3"] = ("SQL Server شبانه به S3",
            "پشتیبان کامل SQL Server هر شب ساعت ۱، رمزنگاری‌شده، به فضای سازگار با S3. ۱۴ روزانه و ۱۲ ماهانه نگهداری و هر هفته آزمایش بازیابی."),
        ["SQL Server point-in-time 1/3: full (weekly)"] = ("SQL Server بازیابی لحظه‌ای ۱ از ۳: کامل (هفتگی)",
            "پشتیبان کامل هر یکشنبه ساعت ۱ که زنجیرهٔ بازیابی را شروع می‌کند (COPY_ONLY خاموش). با قالب‌های تفاضلی و لاگ استفاده کنید."),
        ["SQL Server point-in-time 2/3: differential (daily)"] = ("SQL Server بازیابی لحظه‌ای ۲ از ۳: تفاضلی (روزانه)",
            "پشتیبان تفاضلی هر روز ساعت ۱:۳۰ (تغییرات از آخرین پشتیبان کامل)."),
        ["SQL Server point-in-time 3/3: log (hourly)"] = ("SQL Server بازیابی لحظه‌ای ۳ از ۳: لاگ (ساعتی)",
            "پشتیبان لاگ تراکنش هر ساعت (مدل بازیابی FULL). همراه با کارهای کامل و تفاضلی."),
        ["PostgreSQL nightly"] = ("PostgreSQL شبانه",
            "pg_dump از همهٔ پایگاه‌داده‌ها (یا موارد انتخابی) هر شب ساعت ۱:۳۰، فشرده با zstd و رمزنگاری‌شده."),
        ["MySQL / MariaDB nightly"] = ("MySQL / MariaDB شبانه",
            "mysqldump سازگار (تک‌تراکنش) هر شب ساعت ۱:۱۵، فشرده با zstd و رمزنگاری‌شده."),
        ["Redis snapshot every 6 hours"] = ("Redis هر ۶ ساعت",
            "اسنپ‌شات RDB (با redis-cli ‎--rdb) هر ۶ ساعت. دو روز نگهداری می‌شود."),
        ["SQLite application database hourly"] = ("پایگاه‌دادهٔ SQLite برنامه، ساعتی",
            "فایل‌های SQLite هر ساعت با API پشتیبان آنلاین کپی می‌شوند؛ برنامه می‌تواند در حال کار باشد."),
        ["Documents daily to NAS"] = ("اسناد روزانه به NAS",
            "اسناد کاربر هر روز ساعت ۲۰ به اشتراک شبکه (UNC)، با Shadow Copy برای فایل‌های باز."),
        ["File server share every 2 hours (incremental)"] = ("فایل‌سرور هر ۲ ساعت (افزایشی)",
            "پوشهٔ اشتراکی شرکت: فایل‌های تغییرکرده هر ۲ ساعت در طول روز، نسخهٔ کامل هر یکشنبه. ۳۰ روز و یک سال نسخهٔ ماهانه نگهداری می‌شود."),
        ["Large folders deduplicated to S3"] = ("پوشه‌های بزرگ با حذف تکرار به S3",
            "داده‌های بزرگ و کم‌تغییر (آرشیو، ایمیج ماشین مجازی، رسانه): هر شب فقط قطعه‌های جدید آپلود می‌شوند و پشتیبان روزانه فضای کمی می‌گیرد."),
        ["Photos and media weekly to OneDrive"] = ("عکس و ویدیو هفتگی به وان‌درایو",
            "عکس‌ها و ویدیوها هر یکشنبه ساعت ۳ به وان‌درایو، بدون فشرده‌سازی (رسانه از قبل فشرده است)."),
        ["Windows server configuration weekly"] = ("تنظیمات سرور ویندوز، هفتگی",
            "تنظیمات IIS، کارهای زمان‌بندی‌شده، گواهی‌ها و کلیدهای رجیستری انتخابی هر شنبه ساعت ۴: آنچه برای ساخت دوبارهٔ سرور لازم است."),
        ["Docker volumes nightly"] = ("والیوم‌های Docker شبانه",
            "والیوم‌های نام‌دار Docker هر شب ساعت ۳:۳۰ (در صورت نیاز با توقف کانتینرها برای سازگاری)."),
        ["Hyper-V virtual machines weekly"] = ("ماشین‌های مجازی Hyper-V هفتگی",
            "خروجی ماشین‌های مجازی انتخابی هر شنبه ساعت ۱ (ماشین‌های روشن از Production checkpoint)، با حذف تکرار تا بلاک‌های بدون تغییر دوباره آپلود نشوند."),
        ["Off-site copy of another job (3-2-1)"] = ("نسخهٔ خارج از محل از کار دیگر (۳-۲-۱)",
            "پشتیبان‌های یک کار دیگر را، همچنان رمزنگاری‌شده، هر شب ساعت ۵ به مقصد دوم کپی می‌کند. کار مبدأ را در زبانهٔ مبدأ تعیین کنید."),
        ["Website to NAS + Telegram archive"] = ("وب‌سایت به NAS + آرشیو تلگرام",
            "وب‌سایت هر شب ساعت ۲ به اشتراک شبکه (برای بازیابی) و به کانال خصوصی تلگرام به‌عنوان نسخهٔ اضطراری."),
    };

    public string NameFor(bool persian) => persian && Persian.TryGetValue(Name, out var fa) ? fa.Name : Name;

    public string DescriptionFor(bool persian) => persian && Persian.TryGetValue(Name, out var fa) ? fa.Description : Description;

    internal static bool HasPersian(string name) => Persian.ContainsKey(name);
}
