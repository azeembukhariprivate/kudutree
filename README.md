<p align="center">
  <img src="docs/logo-512.png" alt="KUDUTREE logo" width="128">
</p>

<h1 align="center">KUDUTREE</h1>

<p align="center">See what's taking up space on Windows: every folder and file, biggest first, with a treemap. Free and open source.</p>

---

## Features

- **Start from a drive or a folder**: cards for every drive with used and free space, a folder picker, or a typed path (including network shares like `\\nas\share`).
- **Two ways to scan**:
  - **Fast scan** for whole NTFS drives: reads the drive's file index (the MFT) directly, so a full drive with a million files takes seconds, and every folder is counted, including ones you couldn't open. It asks for administrator rights when you start it.
  - **Folder listing** for everything else (folders, USB drives, network shares, or when you'd rather not give administrator rights): several folders are listed at once, and you can look around while it runs.
- **The folder tree**, biggest first: size, a bar for each item's share of its folder, the number of files, and the date of the newest file inside. Sort by name, size, files or date. Big folders show their 500 biggest items, with the rest in one row that opens more.
- **The treemap**: every file is a rectangle sized by the space it takes, inside rectangles for its folders, shaded as cushions and coloured by file type. Click a rectangle to find the file in the tree; the selected item is outlined in the treemap. Point at it to see what it is.
- **File types**: total size and count for every extension (and what kind of file it is: video, pictures, documents…), in the same colours as the treemap. Click a type to show only those files in the treemap.
- **Largest files**: the 1,000 biggest files anywhere in the scan.
- **Search**: part of a name or a pattern (`*.iso`, `IMG_????.jpg`, several separated by `;`), with filters for size (1 MB to 1 GB or more), date (changed recently, or not changed for a year or three) and files or folders.
- **Old & empty**: files not opened or changed for one to five years (biggest first, with a minimum size), and empty folders (an empty folder holding only empty folders counts once), which can all be moved to the Recycle Bin in one go.
- **Duplicates**: sets of identical files, by the space the extra copies waste. Files are compared by size, then their first 64 KB, then their whole contents (SHA-256). Hard links and online-only OneDrive files (reading those would download them) are left out.
- **Save and compare scans**: save a scan (`.kudutree`) to open again later without scanning, or to compare with a later scan: the **Changes** tab shows what grew, shrank, appeared or went away since then, biggest change first. Saved scans of the same place are offered automatically.
- **Size on disk or file size**, switched with one click. Size on disk is the space really used on the drive: compressed and sparse files count what they use, online-only OneDrive files count nothing, small files kept inside the file index count nothing, and hard links count once.
- **Do something about it**: open, show in File Explorer, copy the path, move to the Recycle Bin, or delete permanently (always asked first). Sizes update straight away without scanning again; "Scan this folder again" refreshes one folder.
- **Show only one folder** and go back up, without scanning again.
- **Right-click a folder or drive** in File Explorer → *Show sizes with KUDUTREE* (optional, chosen when installing or in Settings).
- Light and dark themes, following Windows.

## Download

Get the latest version from the [Releases](../../releases/latest) page:

- `KUDUTREE-x.y.z-setup.exe` installs KUDUTREE for your Windows account (no administrator rights needed to install).
- `KUDUTREE-x.y.z-portable.zip` runs without installing: unzip it anywhere and open `KUDUTREE.exe`.

Requires Windows 10 or 11 (the .NET Framework 4.8 it uses is already part of Windows).

## Privacy

KUDUTREE only reads your drives; nothing is changed unless you delete something yourself. The only network request it makes is an optional check for a newer version on GitHub, which can be turned off in Settings.

## Build from source

You need the [.NET SDK](https://dotnet.microsoft.com/download) (8 or later). The screens are built in C# and the themes are loaded at startup, so the project builds with `dotnet` on any operating system; the app itself runs on Windows.

```
dotnet run --project tests/KUDUTREE.Tests
dotnet build src/KUDUTREE/KUDUTREE.csproj -c Release -o out/KUDUTREE
out\KUDUTREE\KUDUTREE.exe --self-test
```

The tests build a small NTFS drive image byte by byte (folders, hard links, short and long names, compressed files, extra data streams, files whose details are spread over several index records, deleted files and files whose folder is gone) and check the fast scan against it; scan real folders with the folder listing; and check the treemap layout, search, largest files, file types, deleting, saving and opening scans, comparing them, duplicates, and old and empty items. `--self-test` needs administrator rights: it creates a virtual drive (VHD) with known files (including compressed and sparse files, a hard link and a junction), scans it both ways and checks every size against what was written and against each other, runs a fast scan of the system drive, then uses every screen (tree, treemap, file types, largest files, search, old and empty, duplicates, saving, opening and comparing scans, delete, scan again) in both themes, writes `self-test.log` and exits. To build the installer, install [Inno Setup 6](https://jrsoftware.org/isinfo.php) and compile `installer/KUDUTREE.iss`.

## Publishing a new version

1. Change `<Version>` in `src/KUDUTREE/KUDUTREE.csproj` (and `MyAppVersion` in `installer/KUDUTREE.iss`).
2. Commit and push to `main`.
3. GitHub Actions tests and builds the app. When no release exists yet for that version, it creates the `v` tag and publishes the installer and portable zip on the Releases page. Pushes that keep the same version only build.

## License

[MIT](LICENSE). See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
