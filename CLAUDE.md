# KUDUTREE

See what's taking up space: folder tree, treemap, file types, duplicates, saved scans.
Part of the KUDU family of free, open-source (MIT) Windows apps by Azeem Bukhari.

## Working with Azeem (the owner)

- **Branches:** work on `main` and push to `main`. Ask before creating any other branch.
- **New features:** first say what you understood, and ask your doubts and suggestions, one question at a time.
  Only start building when Azeem says "build".
- **Look:** keep the app's current look. No redesigns unless asked.
- **About window credit:** "Made by Azeem, with Claude and ♥" (the heart in the accent colour). Keep it.
- **Commits:** author `Azeem Bukhari <12558389+azeembukhariprivate@users.noreply.github.com>`. Clear messages that say
  what changed and why. Don't put AI model names or versions in commits, code or docs.
- **Words:** the app's text, README and release notes use plain everyday words, not technical jargon.
- **Before pushing:** build and run the tests locally, and re-read the diff. After pushing, watch the GitHub Actions
  build until it's green; fix failures, never skip or disable a test to get green.

## How the app is built

- .NET Framework 4.8 WPF app. The screens are built in C# (no XAML compiler); the themes (`Themes/Light.xaml`,
  `Dark.xaml`, `Styles.xaml`) are XAML loaded at startup as embedded resources, using resource keys such as `Accent`,
  `Text`, `SurfaceBg`. Use the helpers in `Controls/Ui.cs` and existing keys rather than hard-coded colours.
- It builds with the plain .NET SDK on any OS (Microsoft.NETFramework.ReferenceAssemblies); it runs on Windows 10/11.
- Logic that doesn't need Windows lives in `Core/` and is tested by `tests/KUDUTREE.Tests` (a .NET 8 console program).
- `KUDUTREE.exe --self-test` drives the real app on Windows (GitHub Actions runs it, keeping screenshots of every window as
  a build download). Add a self-test step for new screens and features.
- Settings live in `HKEY_CURRENT_USER\Software\KUDUTREE`. The Inno Setup installer (`installer/KUDUTREE.iss`) installs per
  user, without administrator rights.

```
dotnet run --project tests/KUDUTREE.Tests -c Release
dotnet build src/KUDUTREE/KUDUTREE.csproj -c Release -o out/KUDUTREE
out\KUDUTREE\KUDUTREE.exe --self-test        (Windows only; needs administrator rights)
```

## Notes

- Safe to clean (1.2): `Core/Cleanup.cs` lists the spots (exact folders under `Places`, so tests use made-up ones),
  measures each as top-level items (skipping links, folders holding links, and items newer than `MinAge`), and cleans
  only items strictly inside a spot's folders (`Belongs`): your own files via a recycle callback in batches of 200
  (a failed batch is retried one by one), Windows-wide ones permanently. `Views/CleanView.cs` is the page;
  `Recycler` moves batches to the Recycle Bin with SHFileOperation (Windows asks before anything can't go there) and
  reads/empties it. Windows.old and old system parts are only measured and left to Windows' cleanup (cleanmgr).

- Other languages (1.3): the shared `Lang`/`LangUi` (kudu-common) put each window into the chosen language as it's shown.
  Rows drawn from templates (`Views/Rows.cs`) aren't reached by that, so their texts call `Lang.T` in code (file names
  never go through it). Texts are in `src/KUDUTREE/Lang/<code>.txt`, made and checked with kudu-common's
  `tools/lang.py`; when adding or changing a text, add it to every language file (`tools/lang.py check ../kudutree`).

## Shared code (kudu-common)

The files listed in `.kudu-common` (the look and controls in `Controls/`, `Core/Admin.cs`, `AppInfo.cs`, `Args.cs`,
`ThemeManager.cs`, `Unelevated.cs`, `UpdateChecker.cs`, `Views/Dialogs.cs`, `Views/Screenshots.cs`, ...) are copied from
[kudu-common](https://github.com/azeembukhariprivate/kudu-common) and start with a comment saying so.
**Don't edit them here**: change them in kudu-common, then run `python sync.py ../kudutree` there and commit both repos.
The build fails when a copy differs from kudu-common. App-specific details go in `Core/AppDetails.cs`.

## Releasing

1. Raise `<Version>` in `src/KUDUTREE/KUDUTREE.csproj` (and `MyAppVersion` in `installer/KUDUTREE.iss`).
2. Commit and push to `main`.
3. GitHub Actions (`.github/workflows/build.yml`, Windows) tests and builds the app. When no release exists yet for that
   version it creates the `v` tag and publishes the installer and portable zip on the Releases page, plus a copy named
   `KUDUTREE-setup.exe` that the README's download button links to (`releases/latest/download/KUDUTREE-setup.exe`).
   Pushes that keep the same version only build.

## Special rule

- Deleting must stay safe: Recycle Bin by default, permanent deletion always asked first, and system files and
  folders are blocked.
