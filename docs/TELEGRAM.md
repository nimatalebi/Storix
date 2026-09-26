# Backups to Telegram (or Bale)

Storix can store backups as documents in a private Telegram channel or group. This is handy as a free,
off-site copy for small and medium backups. Always turn on **encryption** for these jobs: anyone in the chat
can download the files.

## 1. Create the bot and the chat

1. In Telegram, talk to **@BotFather**, send `/newbot` and copy the **bot token** (`123456789:AA...`).
2. Create a **private channel** (or a group) for the backups.
3. Add the bot as an **administrator** with the rights *Post messages*, *Delete messages* and *Pin messages*.
4. Find the **chat id**: post any message in the channel, then open
   `https://api.telegram.org/bot<token>/getUpdates` (or forward a message to @userinfobot). Channel ids look like
   `-1001234567890`.

## 2. Add the destination

In the job editor: **Destinations → Add… → Telegram**, then set:

| Setting | Value |
|---|---|
| Service | `Telegram` (or `Bale`, which offers the same Bot API) |
| Bot token | the token from BotFather (stored encrypted) |
| Chat id | e.g. `-1001234567890` |
| API base URL | empty for direct access, or your relay URL (see below) |
| Relay key | the relay's `RELAY_KEY` when you use a relay |
| Part size (MB) | `47` (keep it under the 50 MB upload limit) |

Click **Test connection**.

## How it works

- Telegram is an **archive-only** copy, meant for disasters: Storix uploads to it and applies retention, but it
  never downloads from it. Normal restores, restore drills, copy jobs and deduplication use the job's other
  destinations, so give such jobs a second destination (NAS, S3, ...).
- Files up to 47 MB are sent as one document. Larger files are sent as `name.001`, `name.002`, … (the official
  Bot API accepts uploads up to 50 MB), without notification.
- Bots cannot read the history of a chat, so Storix keeps a small **catalog** (`storix-catalog.json`) with the
  list of files and their message ids, sent to the chat and **pinned**. Retention uses it to delete old backups
  (their messages are deleted too). Do not unpin it.
- One Storix installation per chat: jobs on the same machine can share a chat, but two machines writing to the same
  chat can overwrite each other's catalog.

## Restoring from Telegram

1. In the Telegram app (desktop or phone), download all files of the backup into one folder: every part
   (`.001`, `.002`, …) and the `.sha256` file. The app has no 20 MB limit.
2. In Storix Manager: **Restore → From a backup file**, choose the `.001` file (or the file itself when there are
   no parts). Storix joins the parts, checks the SHA-256 and restores. Without the manager:
   `storix restore backup.zip.aes.001 --to D:\restore --password-env STORIX_PW`.

## Limits

- With your own [local Bot API server](https://github.com/tdlib/telegram-bot-api), point *API base URL* at it and
  raise the part size (up to 2000 MB).
- Telegram rate limits (HTTP 429) are respected automatically.

## When the server cannot reach Telegram: Cloudflare Worker relay

If `api.telegram.org` is blocked or unreachable from the server, run the relay in
[`tools/telegram-relay`](../tools/telegram-relay) on Cloudflare Workers (the free plan is enough: requests of up
to 100 MB, 100,000 requests a day; parts are at most 47 MB). The relay only forwards Bot API calls, stores nothing and refuses requests
without the shared key.

**With the Cloudflare dashboard:**

1. *Workers & Pages → Create → Create Worker*, name it (e.g. `storix-relay`) and deploy.
2. *Edit code*, replace everything with the content of `tools/telegram-relay/worker.js` and deploy.
3. *Settings → Variables and secrets*: add a **secret** `RELAY_KEY` with a long random value. Optionally add
   `ALLOWED_BOTS` (your bot id, the digits before `:` in the token) and, for Bale,
   `UPSTREAM = https://tapi.bale.ai`.

**With the command line:**

```sh
cd tools/telegram-relay
npx wrangler deploy
npx wrangler secret put RELAY_KEY
```

In Storix set **API base URL** to the worker URL (e.g. `https://storix-relay.<you>.workers.dev`) and
**Relay key** to the same `RELAY_KEY`. The same two settings exist on Telegram/Bale **notification channels**
(*Settings → Chat and webhook channels*), so notifications can use the relay too.

Security notes: the bot token travels inside the URL to the relay (over HTTPS) and the relay sees the
(encrypted) backup data passing through, so deploy it in your own Cloudflare account. Storix never writes the
token to logs or error messages.
