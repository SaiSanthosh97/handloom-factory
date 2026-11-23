# TD-06 · Release 0.6.0: Owner Also Weaves, Edit Names, Forgot PIN, Start Over

**Status:** Implemented (2026-11-24, C-010). These changes come from the owner's first round of testing.

## Summary
| Feature | Where | Decision |
|---|---|---|
| The owner also weaves: own rate; counted in day entry, reports and payments | Settings → "I also weave sarees" | D20 |
| Edit the factory name and the owner's name | Settings → "Edit factory name and your name" | D21 |
| Forgotten owner PIN: unlock with the phone's screen lock, choose a new PIN | Login → "Forgot PIN?" | Requirements, Login |
| Erase this phone and go back to the first screen | Settings → "Start over on this phone" | D21 |

## Where the code is
| File | Purpose |
|---|---|
| `Core.Factory/Model.cs` | `Employee.Weaves` (optional last parameter, default false), `IsWorker` = worker **or** weaving owner, `IsOwner`; audit actions `FactoryUpdated`, `OwnerWeaves`, `PinReset` |
| `Core.Workflow/AccountService.cs` | `UpdateFactoryAsync`, `SetOwnerWeavesAsync`, `OwnerHasRateAsync`, `OwnerAsync`, `ResetOwnerPinAsync` |
| `Core.Workflow/EmployeeService.cs` | The owner can't be disabled; renaming the owner from Workers also renames them in the factory record |
| `Core.Sync/SyncEngine.Owner.cs` | Invites only for `Role.Employee` (never the owner's record) |
| `Storage.Sqlite` | Migration 4: `ALTER TABLE employee ADD COLUMN weaves INTEGER NOT NULL DEFAULT 0` |
| App `Services/DeviceLock.cs`, `MainActivity.OnActivityResult` | The phone's screen-lock check |
| App `Services/StartOver.cs` | Erase and return to the first screen |
| App Settings, Login and Worker screens | Switch, edit, start-over and forgot-PIN controls; the owner's record hides "Link phone" and "Disable" |

## How it works

### The owner who weaves (D20)
- **One flag, not a second person.** A weaving owner is the same `Employee` record (role Admin) with `Weaves = true`. All "who makes sarees" lists use `IsWorker`, which now includes the weaving owner: day entry, Workers, reports, payments and the weekly notice. All "who may do what" checks still use the role (`IsAdmin`), so the owner keeps every right and one PIN.
  - A separate worker record for the owner was rejected. It would have meant two logins or a hidden link between two people, and two names for the same person in reports.
- **Rate.** The first time it is turned on, the app asks for a rate per saree, valid from the start of the current pay week, so this week's days can be entered. The rate history and later changes work as for any worker, from the owner's Worker page.
- **Turning it off** only hides the owner from new day entry and the lists. Past entries and payments stay, and turning it on again doesn't ask for the rate again.
- **Guards:**
  - The owner can't be disabled (`NotAllowed`), because that would lock the phone.
  - No phone can be linked to the owner's record. Invites check `Role.Employee`; the "Link phone" card is hidden.
  - The owner's entries never reach workers' phones, because the outbox only sends records for linked worker channels. A sync test checks this.
- **Compatibility.**
  - Old databases get the column with default 0.
  - Old export files have no `weaves` field and import as false.
  - Worker phones on older versions ignore the field.

### Edit names (D21)
`UpdateFactoryAsync` writes the factory record and the owner's employee record in one change set. The factory change is also queued to linked worker phones, so their screens show the new factory name. The cached factory and the signed-in user are refreshed.

### Forgot PIN (owner)
1. "Forgot PIN?" first checks the role. On a worker's phone (no owner record) it explains re-linking, as the requirements say.
2. On the owner's phone, if Android has no screen lock (`KeyguardManager.IsDeviceSecure` false), the app explains how to set one. It also says how to clear the app's data as a last resort. **It never resets without a check.**
3. Otherwise `KeyguardManager.CreateConfirmDeviceCredentialIntent` shows Android's own unlock screen (fingerprint, pattern or PIN). The result comes back through `MainActivity.OnActivityResult`.
4. On success, the new PIN is set twice through `ResetOwnerPinAsync`. This also clears any lockout, and the action is audited as `PinReset`.
- **Why this API:** it works on Android 6 to 15 with no extra library. The newer BiometricPrompt needs AndroidX Biometric and a different flow before Android 10. The API is marked obsolete from API 29, but it still works there.

### Start over (D21)
Settings → "Start over on this phone":
1. A warning appears. On the owner's phone it says to export first and that linked worker phones will stop syncing.
2. The person types their PIN. This reuses the login check, so wrong PINs count toward the lockout.
3. `ISyncStore.WipeAsync` deletes everything; it is the same wipe a worker phone does when it is unlinked. The session is signed out, the cached factory is cleared, and the app returns to the first screen.

The database file and its Keystore key are kept. The app's services hold that store, so deleting the file would leave them pointing at a closed database.

## Tests
- **Workflow tests (`OwnerTests`):**
  - weaving: needs a rate; counted and paid; can't be disabled; turning it off and on again;
  - the flag survives reading back from the database and an export → import;
  - editing names: checks; owner only; audited;
  - forgotten PIN reset: the old PIN stops working; audited.
- **Sync test `Owner_who_weaves_stays_on_the_owner_phone_only`:**
  - a worker's phone receives only its own entries;
  - no invite can be created for the owner's record.
- **Not covered by unit tests:** the Android screen-lock check. The owner tests it on the phone.
