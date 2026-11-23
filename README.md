# Handloom Factory

Tracks the sarees each worker in a small handloom factory produces and calculates weekly and monthly wages automatically.

- Android app (Android 6.0 and newer), built with .NET MAUI.
- **No server, no accounts, no monthly cost.** Data lives encrypted on the phone. Worker phones sync with the owner's phone through the free ntfy.sh relay, encrypted.
- Simple login: tap your name and enter a 4-digit PIN.
- Pay week runs Sunday to Saturday, and wages are paid on Saturday.
- Free and open source (MIT).

Requirements: [requirements.md](requirements.md) · Change log: [docs/CHANGES.md](docs/CHANGES.md) · Technical design: [docs/technical](docs/technical/README.md) · Releasing: [docs/RELEASING.md](docs/RELEASING.md)

## Install on a phone

1. On the phone, open **github.com/SaiSanthosh97/handloom-factory/releases** and download the latest `handloom-factory-x.y.z.apk`.
2. Open the downloaded file. If Android asks, allow "Install unknown apps" for your browser or file manager.
3. The app checks for new versions itself every time it opens, and offers to install them. Your data is kept.

## What each version can do

| Version | What you can test |
|---|---|
| 0.1.0 | Owner phone only: set up the factory, PIN login, add, edit and disable workers with rates (including rate changes), enter sarees for any day, dashboard, weekly report, update alerts |
| 0.2.0 | Weekly payments (mark paid), worker history, monthly and yearly reports, export all data and import it again |
| 0.3.0 | Worker phones: link by QR code (or a code sent on WhatsApp), worker PIN, worker enters today's sarees and sees earnings and payments; encrypted sync through ntfy.sh; background sync |
| 0.5.0 | Includes 0.4.0. CSV export of every report (opens in Excel, share by WhatsApp), change history screen (Settings), optional owner notifications through the ntfy app (worker submitted, weekly payments ready, test), Android notification when a new app version is out |
| 0.6.0 | Owner can also weave (Settings → "I also weave sarees"), edit the factory name and your name, "Forgot PIN?" on the login screen (unlock with the phone's screen lock), "Start over on this phone" (back to the first screen with "I'm a worker") |

## Components

| Component | What it does |
|---|---|
| [`Core.Common`](src/Components/Core.Common) | Clock, factory time zone, ids, `Money` (paise), pay-week periods, results and error codes |
| [`Core.Security`](src/Components/Core.Security) | 4-digit PIN hashing (PBKDF2) and lockout; database key wrapping |
| [`Core.Factory`](src/Components/Core.Factory) | Domain: factory, employees, rate history, production entries, audit, wage calculation, entry rules |
| [`Core.Reports`](src/Components/Core.Reports) | Dashboard figures and weekly, monthly and yearly reports |
| [`Core.Workflow`](src/Components/Core.Workflow) | Use cases: setup, login, workers, rates, daily entry, reports (role checks, validation, audit) |
| [`Storage.Sqlite`](src/Components/Storage.Sqlite) | Encrypted SQLite database (SQLite3 Multiple Ciphers), migrations, the factory store |
| [`Core.Relay`](src/Components/Core.Relay) | ntfy.sh client (publish, poll, push notification) |
| [`Core.Sync`](src/Components/Core.Sync) | Owner-hub sync: private encrypted channel per worker, QR linking with key exchange, outbox, conflict rules |
| [`Device.BackgroundWork`](src/Components/Device.BackgroundWork) | Android background job about every 15 minutes |
| [`Device.Vision`](src/Components/Device.Vision) | Reads the link QR code from a photo (ML Kit, on the phone) |
| [`Core.AppUpdater`](src/Components/Core.AppUpdater) | Checks GitHub releases for a newer APK and downloads it, verified by SHA-256 |
| [`Device.Security`](src/Components/Device.Security) | Android Keystore wrapping of the database key |
| [`HandloomFactory.App`](src/Apps/HandloomFactory.App) | The Android app: screens, view models, update alerts |

## Build and test

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet test tests/Core.Common.Tests
dotnet test tests/Core.Factory.Tests
dotnet test tests/Core.Workflow.Tests
dotnet test tests/Core.AppUpdater.Tests
dotnet test tests/Core.Sync.Tests
```

The Android app also needs the MAUI Android workload (`dotnet workload install maui-android`), JDK 17 and the Android SDK:

```sh
dotnet publish src/Apps/HandloomFactory.App -f net10.0-android -c Release
```

## Contributing rules

- Dependencies must use MIT, Apache-2.0, BSD or a similar permissive licence. Record each one in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
- Never commit secrets (the signing key and its password live in `%USERPROFILE%\.handloomfactory`).
- Wage, date and pay-week logic must have unit tests.
- Every requirement change goes into `requirements.md` and `docs/CHANGES.md`.
