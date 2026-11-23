# TD-03 · Release 0.3.0: Worker Phones and Sync

**Status:** Implemented (2026-11-23, C-007). It has unit and multi-phone integration tests; per C-005 the owner tests it on phones at the end.

## Summary
- **Linking.** Workers use the app on their **own phones**. The owner opens a worker and taps **Link phone**, which shows a one-time QR code (valid 15 minutes). The worker's phone taps **I'm a worker → Scan QR code**, or pastes the same code sent on WhatsApp. When the owner's phone confirms, the worker chooses a PIN.
- **The worker's app** shows a big "How many sarees today?" box, their week and month earnings, and recent weeks with payment status. It works offline.
- **Sync.** Data moves through the free **ntfy.sh** relay with **no server of our own**.
  - The owner's phone is the **hub**. Each worker has a **private channel**: its own unguessable topic and its own 256-bit key.
  - A worker's phone only ever receives that worker's own records (D5).
  - Every message is compressed, padded, encrypted (AES-256-GCM) and **signed by the sending phone**.
  - Replays and strangers' messages are dropped.

## Where the code is
| File | Purpose |
|---|---|
| `Core.Security/Crypto.cs` | `AeadBox`, `KeyDerivation` (HKDF), `SigningKey` + `Signatures` (ECDSA P-256), `KeyAgreement` (ECDH P-256), `Proofs` (HMAC), `RandomText`, `Base64Url` |
| `Core.Relay/NtfyRelay.cs` | ntfy client: publish, poll, plain push notification (copied from Travel Vault) |
| `Core.Sync/SyncModel.cs` | `PhoneRole`, `Channel` (per worker), `ISyncStore` |
| `Core.Sync/Wire.cs` | `Envelope`, message kinds, `SyncRecord`, `WireCodec` (encode/decode/pack), `Invite` (QR text) |
| `Core.Sync/OutboxStore.cs` | Wraps the factory store: every change queues its sync records **in the same transaction** |
| `Core.Sync/SyncEngine*.cs` | The cycle; owner side (invites, link acceptance, snapshots, applying workers' entries); worker side (join, applying the owner's records, reconcile, unlink) |
| `Storage.Sqlite` migration 3, `SqliteSyncStore` | `sync_channel`, `outbox`; role, signing key and settings in the encrypted database |
| `Device.Vision/QrReader.cs` | Reads the QR code from a camera photo with ML Kit, on the phone |
| `Device.BackgroundWork` | Android JobScheduler, about every 15 minutes (copied, made to work on Android 6.0) |
| App: `SyncScheduler`, `LinkPhonePage`, `JoinPage`, `WorkerHomePage`, worker tabs | When sync runs; linking screens; the worker's app |

## How it works

### Channels: one per worker (D5)
`Channel(EmployeeId, RelayUrl, Topic, Key, OwnerKey, WorkerKey, State, …, NextSeq, PeerSeq)`:
- **Topic:** 32 random base62 characters (about 190 bits), so it can't be guessed.
- **Key:** a channel key. Messages use `HKDF(key, "msg")` with the label `hf/channel/<employeeId>`, so a message can't be moved to another channel.
- **Pinned keys:** each phone pins the other's ECDSA public key. `NextSeq` and `PeerSeq` give per-sender sequence numbers for replay protection.
- **Owner's phone:** one channel per linked worker.
- **Worker's phone:** exactly one channel. It never has the owner's or other workers' records, and they aren't on its topic either.

### Wire format (adapted from Travel Vault's `WireCodec`)
```
Envelope { v, ch (employee id), from (owner|worker), seq, sent, kind, body }
  JSON → gzip → pad to 384/768/1536/2600 bytes → AES-256-GCM(HKDF(channelKey,"msg"), label "hf/channel/<id>")
  → ECDSA-P256 signature over the ciphertext by the sending phone
text = "hf1.<base64url ciphertext>.<base64url signature>"   (always ≤ 3,800 chars, below ntfy's limit)
```
**On receipt**, a message is used only if every check passes:
1. the signature verifies with the **pinned** key of the other phone;
2. it decrypts with the channel key and label;
3. `from` is the other side;
4. `seq` is greater than the last one seen from that phone.

Otherwise it is silently ignored. That covers the phone's own messages, junk, strangers and replays (tested). Decompression is capped at 1 MB.

Records travel as `SyncRecord(type, json)`, using the same JSON as the export files (money in paise). They are packed into as few messages as fit: the list is split in halves until each half fits.

### Linking a worker's phone
1. **Owner: Link phone.** `CreateInviteAsync` creates the channel (state *Inviting*): topic, key, the owner's public key, an invite id and a 32-byte secret, expiring in 15 minutes. The invite is `HF1:<base64url JSON>`, shown as a QR code (QRCoder) or sent as text.
2. **Worker: scan or paste.** `JoinAsync` checks the code and expiry and that the phone isn't set up, and makes the phone a *Worker* phone. It creates its ECDSA signing key and an **ECDH key-agreement pair**, then publishes `link { inviteId, workerKey, proof = HMAC(secret, inviteId|workerKey), agreementPublic }`.
3. **Owner's phone accepts** if: the invite is this channel's, unused and unexpired; the proof is valid; and the message is signed by the key inside it. It then:
   - creates its own ECDH pair and publishes `linked { workerKey, ownerAgreement }`, still under the invite key;
   - **switches the channel to a new key = HKDF(ECDH(owner, worker), salt = invite key)**;
   - pins the worker's key, marks the channel *Linked*, audits "PhoneLinked" and sends a **full snapshot** under the new key.
4. **Worker's phone** sees `linked`. If `workerKey` is its own, it derives the same new key and reads the snapshot (factory, its own employee record, rates, entries and payments for the last 400 days, shared settings). It becomes *Linked* and the worker chooses a PIN.

**Why the key exchange:** found by a test. Without it, anyone who photographed the QR code during its 15 minutes could also "join" and silently read the worker's data, since the channel key is in the QR. Now a second phone with the same QR stays unconfirmed and can't decrypt anything after the first message (tested: *Expired_or_reused_invites_are_refused*).

### What moves (the outbox)
`OutboxStore` wraps the factory store. On every commit, it adds the sync records the change causes to the **same transaction**, so a change is never saved without its outgoing message, even offline.

| Phone | Sends |
|---|---|
| Owner | To worker W only: W's entries (added, changed, deleted), rates, payments (paid and undone), W's employee record; factory details to all linked workers. Disabling W also queues **revoke**. After an import, a full snapshot to everyone. |
| Worker | Only their own entries, typed by them |

Records that arrive **from** sync are written to the inner store, so they are never echoed back.

### Rules for incoming records
**Owner's phone, receiving from W** (D9, D10). An entry is accepted only if all of these hold:
- it is W's own and typed by W;
- it is within 0–50 sarees;
- **its date is the day it was typed** (the factory-time date of its `UpdatedAt`), so uploading Monday's entry on Tuesday is fine, but claiming "Sunday" on Tuesday is not (tested);
- the owner hasn't set that day (`EnteredBy = Admin` always wins);
- it is newer than the stored copy.

It is then saved and audited "(from worker's phone)".

**Worker's phone, receiving from the owner** (signed by the owner, so trusted): only records about this worker are applied.
- The owner's value for a day always wins, and the worker's screen then shows "The owner has set today's number" (tested).
- After a **complete** snapshot (all parts in one fetch), the worker's phone deletes owner-entered days and payments from the snapshot period that the owner no longer has. It **re-sends** its own entries that the owner doesn't have, so nothing typed on the phone is lost.

### The cycle (`SyncAllAsync`, one channel at a time, under a lock)
```
fetch new messages (since last id) → verify → apply / answer
if last good sync > 11 h ago (ntfy keeps ~12 h) and no request in the last hour → state-request
  (the other phone answers: owner → full snapshot; worker → its own entries of the last 60 days)
flush the outbox (packed); a queued revoke → send "revoked", close the channel
worker: check-in every 6 h (owner shows "last seen")
```
Network errors stop that channel only; the outbox stays and is sent next time (tested offline → online).

**When it runs** (`SyncScheduler`): 3 s after a change, when the app opens or returns, every minute while open, and about every 15 minutes in the background (JobScheduler, also after a restart). While the QR screen is open, the owner's phone checks that worker's channel every 3 seconds, and the worker's waiting screen does the same.

### Unlinking
- **Disable worker, Unlink phone, or Link a new phone** (re-link for a new phone or a forgotten PIN): the owner's phone sends a signed `revoked` and closes the channel.
- **The worker's phone wipes** all factory data, its PIN, the channel and its role, says "This phone was unlinked by the owner" and returns to the first screen (tested).
- If the old phone is offline, it can still never receive anything new: the topic and key are gone.

### Exports and imports
Channel keys are never exported. An owner who imports data on the **same** phone keeps the channels, and `ResendAllAsync` sends every linked worker a fresh snapshot (tested). On a **new** owner phone, workers must be linked again.

## Key decisions
| Decision | Why | Alternatives rejected |
|---|---|---|
| One channel per worker, owner as hub | Workers can't receive each other's wages, not even encrypted | One shared topic (every phone gets everyone's data) |
| ECDH key exchange when linking | A photographed QR is useless after linking | Trust the QR alone (found unsafe by a test) |
| Outbox written in the same transaction | Nothing typed offline can be lost | Send directly after saving (lost when offline) |
| Owner is authoritative; worker entries checked on arrival | Matches D9/D10; a modified worker app can't change other days or override the owner | Last-writer-wins for everyone |
| `UpdatedAt` wall clock instead of a hybrid logical clock | Only two writers per channel and the owner always wins, so simple timestamps are enough | HLC (Travel Vault) – more machinery than needed here |
| Snapshot of 400 days for the worker | The worker's screen needs 12 weeks plus the year; keeps re-sync small | Everything (grows forever) |
| QR read from a photo (ML Kit) and paste as fallback | Same proven approach as Travel Vault; WhatsApp works when apart | Live camera scanner library (more dependencies) |

## Tests: `Core.Sync.Tests` (18), several complete phones through an in-memory relay that expires messages after 12 h
- **Linking:** the worker gets only their own data, logs in, sees the dashboard; expired, invalid and **re-used QR** codes are refused; the invite survives WhatsApp text around it; the owner's phone can't join as a worker.
- **Entries:** worker → owner; an owner correction wins and locks the worker's day; a worker edit after the owner's is ignored; owner-added and deleted days reach the worker; a late upload of yesterday is accepted but a forged other day is not.
- **Privacy and security:** workers never get each other's data and one channel's key can't open another's messages; junk, a stranger's signed message and a replay are ignored; a 300-day history needs several messages, all ≤ 3,800 characters, with ≤ 4 different sizes.
- **Offline:** a worker away 2 days catches up (entries, rates, payment); an owner away 2 days gets the worker's entry again; nothing is lost while offline.
- **Unlinking:** disabling wipes the worker's phone; re-linking cuts off the old phone; an import re-sends snapshots.

## Known limits
- ntfy.sh is a free public service with per-IP daily limits. A small factory's traffic is far below them. A self-hosted ntfy server can be used later (the relay URL is stored per channel).
- A worker who changes the phone's date could backdate an entry by a day. The owner sees "(from worker's phone)" in the log and can correct it (TD-00 §5.5).
- The worker's phone shows the last 12 weeks and the month. Full reports stay on the owner's phone.
