# Releasing an update

The app checks `https://github.com/Duckkyinc/pc-optimizer-/releases/latest` (set in `Core/Updater.cs`).
Any release whose tag is newer than the running version (tags like `v2.4.0`) is offered to users in-app.

## Option A: automatic (GitHub Actions)
1. Commit your changes.
2. `git tag v2.4.1 && git push origin v2.4.1`
3. The "Release" workflow builds `PC-Optimizer.exe` and publishes the release. Done.

## Option B: just upload the zip (easiest)
1. Draft a new release with a tag like `v2.4.1`.
2. Drag in the `PC-Optimizer-v2.4.1.zip` you got (the .exe inside is used).
3. Publish. Everyone's app offers the update within 6 hours, or right away from Settings > Updates.

## Option C: manual build
1. Build `PC Optimizer.exe` (dotnet publish -c Release -o out).
2. On GitHub: Releases > Draft a new release > tag `v2.4.1`.
3. Attach the .exe (or a .zip with the .exe inside) and publish.

The tag and title can be anything. The app uses the version in the zip/exe file name (like `PC-Optimizer-v2.4.1.zip`) if there is one, otherwise the newest published release counts as an update.
