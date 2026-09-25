using System.Net;
using System.Text;
using NT.Storix.Core.Models;

namespace NT.Storix.Core.Security;

/// <summary>Printable page with everything needed to restore a job's encrypted backups.</summary>
public static class RecoverySheet
{
    public static string BuildHtml(BackupJob job, bool includePassword)
    {
        static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

        var p = job.Processing;
        var keyFile = string.IsNullOrWhiteSpace(p.EncryptionKeyFile) ? null : p.EncryptionKeyFile;
        string? keyFileContent = null;
        if (keyFile is not null && File.Exists(keyFile) && new FileInfo(keyFile).Length < 4096)
        {
            keyFileContent = File.ReadAllText(keyFile).Trim();
        }

        var html = new StringBuilder();
        html.Append("""
            <!DOCTYPE html><html><head><meta charset="utf-8"><title>Storix recovery sheet</title>
            <style>
              body{font-family:Segoe UI,Arial,sans-serif;max-width:760px;margin:32px auto;color:#111}
              h1{font-size:22px;margin-bottom:4px} .muted{color:#666}
              table{border-collapse:collapse;width:100%;margin:16px 0} td{border:1px solid #bbb;padding:8px;vertical-align:top}
              td:first-child{width:200px;font-weight:600;background:#f4f4f4}
              .secret{font-family:Consolas,monospace;font-size:16px;word-break:break-all}
              .warn{border:2px solid #b00;padding:10px;margin:16px 0}
              ol li{margin-bottom:6px}
            </style></head><body>
            """);
        html.Append($"<h1>Storix recovery sheet</h1><div class=\"muted\">Printed {DateTime.Now:yyyy-MM-dd HH:mm} on {E(Environment.MachineName)}</div>");
        html.Append("<div class=\"warn\">Keep this sheet in a safe place (for example a safe or a password manager). " +
                    "Without the password" + (keyFile is null ? string.Empty : " and the key file") + ", the encrypted backups cannot be restored by anyone.</div>");
        html.Append("<table>");
        html.Append($"<tr><td>Job</td><td>{E(job.Name)}</td></tr>");
        html.Append($"<tr><td>Job id</td><td>{job.Id}</td></tr>");
        html.Append($"<tr><td>Backup file names</td><td>{E(job.FilePrefix)}_YYYYMMDD_HHMMSS.zip.aes</td></tr>");
        html.Append($"<tr><td>Destinations</td><td>{E(string.Join(", ", job.Destinations.Select(d => d.ToString())))}</td></tr>");
        if (p.EncryptionMode == EncryptionMode.PublicKey)
        {
            html.Append("<tr><td>Encryption</td><td>AES-256-CBC + HMAC-SHA256, data key wrapped with RSA-4096-OAEP (public key)</td></tr>");
            html.Append($"<tr><td>Key fingerprint</td><td class=\"secret\">{E(p.PublicKeyPem is null ? "(none)" : PrivateKeySecret.Fingerprint(p.PublicKeyPem))}</td></tr>");
            html.Append("<tr><td>Private key</td><td>Kept offline (.pem file) with its passphrase: write down where it is stored.<br><br>Location: ________________________________</td></tr>");
        }
        else
        {
            html.Append($"<tr><td>Encryption</td><td>AES-256-CBC + HMAC-SHA256, key from PBKDF2-SHA256 (600,000 iterations)</td></tr>");
            html.Append($"<tr><td>Password</td><td class=\"secret\">{(string.IsNullOrEmpty(p.EncryptionPassword) ? "(none)" : includePassword ? E(p.EncryptionPassword) : "________________________________")}</td></tr>");
        }
        if (keyFile is not null)
        {
            html.Append($"<tr><td>Key file</td><td>{E(Path.GetFileName(keyFile))}<br><span class=\"muted\">{E(keyFile)}</span>" +
                        (keyFileContent is null ? string.Empty : $"<br>Content: <span class=\"secret\">{E(keyFileContent)}</span>") + "</td></tr>");
        }

        html.Append("</table>");
        html.Append("<h2>How to restore</h2><ol>");
        html.Append("<li>Install Storix from <b>https://github.com/nimatalebi/Storix/releases</b>.</li>");
        html.Append("<li>Download the backup (<code>.zip.aes</code>) and its <code>.sha256</code> file from the destination.</li>");
        html.Append("<li>Open <b>Storix Manager → Tools → Restore backup…</b>, choose the file, enter the password" + (keyFile is null ? string.Empty : " and select the key file (recreate it from the content above if needed)") + ".</li>");
        html.Append("<li>For databases, use <b>Restore database…</b> after extracting (SQL Server <code>.bak</code>, MongoDB <code>.archive</code>).</li>");
        html.Append("</ol></body></html>");
        return html.ToString();
    }
}
