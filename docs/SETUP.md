<h1 align="center">
  Qobuz for Lidarr — Setup Guide 🔑
</h1>
<p align="center">
  A <strong>step-by-step walkthrough</strong> that takes you from a Qobuz subscription to <em>automatically downloaded lossless FLAC</em>, including the three settings that silently break everything.
</p>

<br>

## What this guide covers 🗺️

Getting this plugin working means filling in six Qobuz fields and flipping three Lidarr settings that are easy to miss. Credentials alone are not enough: a correctly-authenticated setup still downloads nothing if the download protocol is disabled, and still rejects every release if your quality profile excludes FLAC.

Work through the stages in order. Each one ends with something you can check, so you find out *where* it broke rather than discovering at the end that nothing works.

| Stage | What you do | What proves it worked |
| --- | --- | --- |
| [1](#stage-1--prerequisites-) | Confirm Lidarr supports plugins and your subscription is current | Lidarr shows a **System → Plugins** page |
| [2](#stage-2--install-the-plugin-) | Install from the GitHub URL and restart | Qobuz appears in the installed plugin list |
| [3](#stage-3--get-your-login-credentials-) | Capture your email and hash your password | A 32-character MD5 hash |
| [4](#stage-4--choose-an-app-id-and-secret-) | Leave blank, or use the known-good pair | **Test** passes on the indexer |
| [5](#stage-5--add-the-indexer-) | Create the Qobuz indexer and test it | Green **Test**, indexer saves |
| [6](#stage-6--add-the-download-client-) | Create the Qobuz download client and test it | Green **Test**, client saves |
| [7](#stage-7--the-three-settings-that-block-everything-) | Enable the protocol, allow FLAC, set the path | Protocol shows as allowed |
| [8](#stage-8--verify-end-to-end-) | Search, grab, and confirm the import | FLAC files in your library |

> [!NOTE]
> Every value and failure message in this guide was verified against a live Lidarr instance and a live Qobuz account, not copied from documentation. Where something is a limitation rather than a mistake, it says so.

## Stage 1 — Prerequisites 📦

**Lidarr on the plugins branch.** Plugin support is not in stable Lidarr. You need a build that has a **System → Plugins** page. If that page is missing, no amount of configuration here will help — switch to the `plugins` branch first. The [hotio](https://hotio.dev/containers/lidarr/) and [Lidarr](https://lidarr.audio) docs both cover this.

**A current Qobuz subscription with lossless streaming.** This matters more than it sounds. If your subscription has lapsed, Qobuz does not return an error — it silently answers every download request with a **30-second preview**, for *every* format including FLAC. The plugin detects this and fails the track rather than importing a truncated file as lossless, so the symptom of an expired subscription is *albums that fail*, not short files in your library.

To check: sign in at [play.qobuz.com](https://play.qobuz.com/login) and confirm you can actually play a track in full. A plan that only offers MP3 streaming cannot download FLAC, and [Qobuz's plans](https://www.qobuz.com/) differ by region.

> [!TIP]
> This is the single most common cause of "Test passes but every track fails." Authentication and *entitlement* are separate things — your login can be perfectly valid while your account has no right to stream lossless.

## Stage 2 — Install the plugin 🔌

1. In Lidarr, go to **System → Plugins**.
2. Paste this into the GitHub URL field and click **Install**:

   ```
   https://github.com/jwmarb/Lidarr.Plugin.Qobuz
   ```

3. **Restart Lidarr** when it tells you to. The plugin does not load until you do.

After the restart, **System → Plugins** should list Qobuz. If it instead logs `No releases found`, the repository has no release that Lidarr can read — Lidarr fetches releases *unauthenticated*, so a draft release is invisible to it, and the version tag must parse as a plain version number.

## Stage 3 — Get your login credentials 🔐

The plugin supports two login modes. **Email + MD5 password is the recommended one** — it is simpler, and it avoids a trap that the token mode has (covered in Stage 4).

### Email + MD5 password (recommended)

`Qobuz Email` is just the email address you sign in to Qobuz with.

`Qobuz Password (MD5)` is **not your password** — it is an MD5 hash of it. This is the field people most often get wrong, and a wrong value here produces a login failure that looks identical to a wrong password.

Generate it with whichever you have:

```sh
# macOS / Linux
printf '%s' 'your-password-here' | md5sum | cut -d' ' -f1

# macOS (if md5sum is unavailable)
printf '%s' 'your-password-here' | md5

# Python, anywhere
python3 -c "import hashlib; print(hashlib.md5(input().encode()).hexdigest())"
```

The result is **32 lowercase hexadecimal characters**. Running the first command above against the literal placeholder `your-password-here` gives `5daa329983cb7746e7ebee485cd086f9`, which is a handy way to check your command is shaped right before you substitute your real password. Paste your own result into `Qobuz Password (MD5)`.

> [!WARNING]
> **Use `printf`, not `echo`.** Plain `echo` appends a newline, which gets hashed too and produces a completely different hash. Both look like valid 32-character hashes, but only one works:
>
> ```
> printf '%s' 'correct horse battery' → 88e4ddd2402d92d50e1879d6ecd9ffd4   ✅
> echo 'correct horse battery'        → 37d24349f6dc31d9ac757441258c4a7a   ❌
> ```
>
> If you use `echo`, add `-n`. Beware of online MD5 generators for the same reason — many add a trailing newline, and you have also just typed your password into someone else's website.

Leave `User ID` and `User Auth Token` empty. If you fill in both pairs, the email and password win.

### User ID + Auth Token (alternative)

Use this only if email login fails for your account. Both values come from one API call — substitute your email and the MD5 hash from above:

```sh
curl -s -G 'https://www.qobuz.com/api.json/0.2/user/login' \
  --data-urlencode 'email=you@example.com' \
  --data-urlencode 'password=YOUR_MD5_HASH' \
  --data-urlencode 'app_id=712109809' \
  -H 'X-App-Id: 712109809' \
  | python3 -c 'import json,sys; d=json.load(sys.stdin); print("User ID:", d["user"]["id"]); print("Auth Token:", d["user_auth_token"])'
```

That prints the two values labelled, ready to paste into `User ID` and `User Auth Token`. You can also read them out of the web player's network requests in your browser's developer tools, but the call above is less error-prone. If it returns an error instead, the login itself failed — fix that first, using Stage 5's message table.

> [!IMPORTANT]
> **An auth token is bound to the App ID that minted it.** Verified directly: a token created under app id `712109809` works under `712109809` and fails with HTTP 400 under `798273057`. So if you use token login, the `App ID` in Stage 4 must be the *same one you used to mint the token* — and if you change the App ID later, you must mint a new token. Email + MD5 has no such coupling, which is why it is recommended.

## Stage 4 — Choose an App ID and Secret 🔑

`App ID` and `App Secret` are Qobuz's **API client credentials**. They identify the *application* talking to the API — the web player, the mobile app — not you. They are the same values for everybody, they are not per-user secrets, and you do not register for them.

### Try empty first

**Leave both fields blank.** The plugin then scrapes the web player's pair out of `play.qobuz.com`'s JavaScript bundle, exactly as the official web player does. Most accounts work this way, and there is nothing to obtain.

### If that fails, use the known-good pair

Some accounts are rejected by the web player's app id with an HTTP 401. If **Test** fails, or if **Test** passes but every track later fails to download, fill in the mobile client's pair:

| Field | Value |
| --- | --- |
| `App ID` | `712109809` |
| `App Secret` | `589be88e4538daea11f509d29e4a23b1` |

This pair is verified end to end against a live Qobuz Studio subscription: login, request signing, and a complete album imported as real lossless FLAC.

### Why this is fiddly

- **The id and secret are a matched pair.** Qobuz signs download-URL requests using the secret, so an id combined with the *wrong* secret logs in happily and then fails every media request with `Invalid Request Signature`. Never mix an id from one pair with a secret from another. The plugin also rejects one without the other.
- **The web pair is public, the mobile pair is not.** The web player's id and secret ship in a public JavaScript bundle, which is why they can be scraped. `712109809` is not in that bundle — it comes from Qobuz's mobile client, and is the value independent projects such as [SpotiFLAC](https://github.com/spotbye/SpotiFLAC) and [musicdl](https://github.com/CharlesPikachu/musicdl) use for the same reason.

## Stage 5 — Add the indexer 📡

1. Go to **Settings → Indexers** and click **+**, then choose **Qobuz**.
2. Fill in the fields from Stages 3 and 4.
3. Click **Test**.

A green check means Qobuz accepted your credentials. If it fails, the message tells you which problem you have:

| Message | Meaning | Fix |
| --- | --- | --- |
| `Supply either an email and MD5 password, or a user id and auth token. The password must be an MD5 hash, not the password itself.` | A login pair is incomplete | Fill in *both* halves of one pair |
| `Qobuz rejected these credentials: ...` plus `try setting a different App ID and App Secret` | Qobuz refused the login | Re-check the MD5 (Stage 3), then set the App ID pair (Stage 4) |

4. **Save.**

> [!NOTE]
> A green **Test** proves your account can log in and search. It does **not** prove the account can download media — login and search are unsigned requests, while download URLs are signed with the App Secret. This is exactly why a mismatched App ID/Secret pair passes **Test** and then fails every track.

## Stage 6 — Add the download client 💾

1. Go to **Settings → Download Clients** and click **+**, then choose **Qobuz**.
2. Set `Download Path` to a folder Lidarr can write to, and that is visible *inside* its container if you run Docker. This is a staging folder — Lidarr imports the files into your library afterwards and leaves it empty.
3. Optionally enable `Save Synced Lyrics` (needs `lrc` added under **Settings → Media Management → Import Extra Files**) and `Use LRCLIB as Lyric Provider`, since Qobuz supplies no lyrics.
4. Click **Test**, then **Save**.

If the path does not exist you get `Folder does not exist` on the `Download Path` field.

> [!TIP]
> This client has **no credential fields of its own** — it reuses the credentials from the Qobuz *indexer* that produced the release. So credentials only ever get entered once, in Stage 5. If you ever see `Qobuz credentials are unavailable`, it means the release did not come from a configured Qobuz indexer.

## Stage 7 — The three settings that block everything 🚧

Credentials are correct and both tests are green, and downloads still may not happen. These three are the reason, and none of them produces an obvious error.

### 1. Enable the Qobuz download protocol ⚠️

**This is the big one.** Lidarr registers the Qobuz protocol but defaults it to **not allowed**, so every grab is silently blocked.

Go to **Settings → Profiles → Delay Profiles**, edit your profile, and make sure **Qobuz** is enabled. Verified on a live instance: this ships as `allowed=false`.

### 2. Allow FLAC in your quality profile

Under **Settings → Profiles → Quality Profiles**, your profile must allow FLAC, or Lidarr rejects every Qobuz release as an unwanted quality. FLAC lives inside the **Lossless** group, so expand it rather than scanning the top level.

Verified on a live instance: the stock **Any** and **Lossless** profiles both allow FLAC, but the stock **Standard** profile does **not**. If your artists use **Standard**, either switch profiles or enable FLAC in it.

### 3. Make the download path reachable

`Download Path` is only checked for *syntax* when you save. Its existence and writability are checked by **Test** — so if you skipped **Test** in Stage 6, a bad path will not surface until a download fails. Run **Test**.

## Stage 8 — Verify end to end ✅

Configuration being valid is not the same as the pipeline working. Prove it:

1. Pick an album in your library that Qobuz definitely has.
2. Trigger a **manual search** for it.
3. Confirm Qobuz releases appear, titled like `Artist - Album (Year) [FLAC Lossless] [WEB]`.
4. **Grab** one and watch **Activity → Queue**.
5. Confirm it reaches **Imported**, then check the files landed in your library.

A verified run looks like this: the queue item progresses through `downloading` with its remaining size falling, then disappears from the queue as Lidarr imports it, and **History** shows `downloadImported` plus one `trackFileImported` per track. The download folder ends up *empty* — that is correct, because the files were moved into your library.

Spot-check a file to confirm it is genuinely lossless:

```sh
ffprobe -hide_banner "/path/to/your/library/Artist/01 - 01 - Track.flac"
```

Expect a full track duration and roughly `1000+ kb/s` at `44100 Hz`. A duration near **30 seconds** means Qobuz served a preview — go back to Stage 1 and check your subscription.

## Troubleshooting 🔧

| Symptom | Most likely cause |
| --- | --- |
| No **System → Plugins** page | Lidarr is not on the plugins branch (Stage 1) |
| `No releases found` when installing | Lidarr reads releases unauthenticated; drafts are invisible (Stage 2) |
| **Test** fails with `rejected these credentials` | Wrong MD5, or the account needs the App ID pair (Stages 3–4) |
| **Test** passes, but **every** track fails | Mismatched App ID/Secret, or a lapsed subscription (Stages 4, 1) |
| Tracks fail with `returned a preview sample` | Subscription has no lossless entitlement (Stage 1) |
| Grab succeeds, nothing ever appears in the queue | Qobuz protocol not allowed in the delay profile (Stage 7) |
| Releases found but always rejected | Quality profile excludes FLAC (Stage 7) |
| `Folder does not exist` | Download path missing or not visible in the container (Stage 6) |
| `Qobuz credentials are unavailable` | Release did not come from a configured Qobuz indexer (Stage 6) |
| Token login suddenly stops working | App ID changed; tokens are bound to the app id that minted them (Stage 3) |
| Album reported `Failed` after some tracks worked | Expected: a partial album fails so Lidarr can retry elsewhere |

## Keeping your credentials safe 🔒

Your MD5 password hash is a credential — it is what the API accepts in place of your password, so treat it like one. Do not paste it into issues, logs, or online hash tools. If you keep a local `.env` for testing, make sure it is gitignored before you ever run `git add -A`.

The App ID and App Secret are **not** personal secrets — they identify the client application and are identical for every user, which is why this guide can print them.

---

Back to the [README](../README.md) for architecture, settings reference, and known limitations.
