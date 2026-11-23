# Releasing an Update

Phones running Handloom Factory check for a new version **every time the app is opened or brought back** (at most once an hour) (D16). They read the latest release of **github.com/SaiSanthosh97/handloom-factory**, show a pop-up and a dashboard banner, download the `.apk`, verify it against GitHub's SHA-256, and hand it to Android's installer.

## Steps for each new version

1. **Bump the version** in `src/Apps/HandloomFactory.App/HandloomFactory.App.csproj`:
   - `ApplicationDisplayVersion`: the version users see, e.g. `0.1.0` → `0.2.0`. It **must match the release tag** (`v0.2.0`), because the app compares them.
   - `ApplicationVersion`: a whole number that must **go up by one** with every release. Android refuses to "update" to a lower or equal number.
2. **Record the change** in `docs/CHANGES.md`, `requirements.md` and the technical docs.
3. **Test and build:**
   ```sh
   dotnet test tests/Core.Common.Tests     # and every other test project
   dotnet publish src/Apps/HandloomFactory.App -f net10.0-android -c Release
   ```
   The release build is signed automatically with the key in `%USERPROFILE%\.handloomfactory\` (see below). It contains 64-bit and 32-bit ARM code, so it also runs on older phones.
4. **Commit and push**, then **publish the release** with the APK attached. The file name must end in `.apk`:
   ```sh
   copy src\Apps\HandloomFactory.App\bin\Release\net10.0-android\publish\io.github.saisanthosh97.handloomfactory-Signed.apk handloom-factory-0.2.0.apk
   gh release create v0.2.0 handloom-factory-0.2.0.apk --title "Handloom Factory 0.2.0" --notes "What's new…"
   ```

Phones offer the update the next time the app is opened.

## The signing key: back it up

`%USERPROFILE%\.handloomfactory\release.keystore` and `signing.props` (which holds its password) sign every release.
- **Android only installs an update signed with the same key** as the installed app. If this key is lost, no installed phone can ever be updated again. Everyone would have to uninstall, which deletes the data on the phone, and reinstall. Export the data first (from 0.2.0).
- Keep a copy of that folder somewhere safe and private, such as an encrypted USB stick or a password manager. **Never commit it.**

## Notes
- The repository must stay **public**, because the app reads releases without a login. Only the app and its source code are public; factory data never leaves the phones.
- Emulator test builds: `-p:EmulatorBuild=true` adds x86-64 code.
