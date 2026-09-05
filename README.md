# VRC Image Curator

VRC Image Curator is a local Windows tool for reviewing duplicate and similar images collected by VRCX. It scans the fixed `Emoji`, `Prints`, and `Stickers` categories, keeps unique images organized, and places possible matches in a persistent visual review queue.

## Safety model

- Filenames and metadata do not determine image identity.
- Exact matches use normalized decoded pixels, dimensions, GIF frame order, and frame timing.
- **Images that match an archived image 100% are resolved automatically**: the archived copy is kept and the incoming copy goes to the Windows Recycle Bin, without asking. See *Automatic duplicate resolution* below.
- Anything below 100%, and any 100% match at a different resolution, is always reviewed by you.
- Images are processed in path order. Each unique image moves into the archive immediately, so later files in the same scan are compared against the updated archive.
- Possible matches remain in their incoming folders while the Review queue stores references to them; scanning does not create or move a review copy.
- Clearing the Review queue leaves those incoming files untouched. Reviews created by older versions are still returned safely from the legacy holding folder.
- `Keep incoming` and `Keep match` use the Windows Recycle Bin for the image that is not kept. Permanent deletion is never used as a fallback.
- Completed operations and resolved reviews are compacted automatically; the activity history retains the newest 1,000 entries.
- Oversized or unusually frame-heavy images are rejected before full decoding to protect application memory.
- Interrupted moves and recycle requests are recorded in a durable operation journal and reconciled on restart.
- Archive fingerprints are stored locally and reused when a file's path, size, and modification time are unchanged. Every scan refreshes additions, removals, and changed files before matching.
- Settings save automatically when a field is committed. A source folder that does not exist yet is reported inline rather than refused, so a folder that appears later still works; only overlapping source and output folders block a save. Scanning creates the output folder on demand and never creates a source folder.
- Tests use temporary folders and never access the configured VRCX or archive folders.

## Automatic duplicate resolution

An incoming image is resolved without asking only when it is the *same picture* as one already
in the archive. That means either byte-identical decoded pixels, or a similarity score of 100%
**at the same resolution and frame count**. In that case the archived copy is kept and the
incoming copy is moved to the Windows Recycle Bin.

Three deliberate limits on this:

- A 100% match at a **different resolution** is never automatic. Choosing between a larger and a
  smaller copy is your decision, so it goes to the review queue.
- The archived copy's fingerprint is re-verified immediately before the incoming copy is
  discarded, so the last remaining copy of an image can never be thrown away.
- If the Recycle Bin is unavailable, or anything else fails, the image is queued for review
  instead. Permanent deletion is never used as a fallback.

Recovering an automatically resolved image means restoring it from the Recycle Bin. Every one is
recorded in **History**.

## Supported files

PNG, animated GIF, JPG/JPEG, WebP, and BMP are supported. MP4, `.temp`, and unsupported files stay untouched.

## Getting started

1. Run `VrcImageCurator.exe`.
2. Open **Settings**.
3. Choose the source folders and enable the categories you want to use.
4. Choose one main output folder. The default is `Archived Images` inside your `Pictures\VRChat` folder.
5. Settings save automatically as you change them. Select **Scan now** when you are ready; the output folder is created if it does not exist yet.
6. Use **Scan another folder** for a one-time recursive scan outside the configured VRCX folders.
7. Use **Start watching** when you want the app to monitor configured folders during the current session. Choose **OnDetection** to analyze each image as it arrives, or **OnInterval** to ignore individual arrivals and sweep the folders on a timer instead (default 60 seconds, range 15–3600). Temporarily unavailable folders are attached automatically when they return.
8. Review matches with **Keep incoming**, **Keep match**, or **Move as Unique**.
9. **Stop** interrupts a running scan. It stops between images, never during one, so nothing is left half-moved.

Suggested VRCX source root:

```text
C:\Users\<you>\Pictures\VRChat
```

The default category folders are `Emoji`, `Prints`, and `Stickers` beneath that root, and the default archive is `Archived Images` beside them. The Pictures folder is located through Windows, so a Pictures folder redirected to OneDrive is found correctly.

The app suggests category folders beneath that root. Source paths remain editable. New files are written beneath the single output root while preserving their category-relative path. For example, `Emoji\2025-05\image.png` moves to `<output>\Emoji\2025-05\image.png`.

**Start with Windows** launches the app in background watching mode. Ordinary launches begin with watching stopped.

## Portable installation and removal

No installer is required. Keep the executable anywhere you can write and run it.

The executable is not code-signed, so Windows SmartScreen shows *"Windows protected your PC"* the
first time you run a downloaded copy. Choose **More info > Run anyway** if you trust the source.
The app makes no network connections of any kind: it contains no HTTP client and no sockets, and
everything it reads or writes is on your own disk.

Before removing the executable, use **Settings > Clear local data** if you also want to remove settings, index, queue, and history. Clearing stops folder monitoring and Windows startup registration, and is blocked while a review or file operation still needs the state.

If a file operation needs manual attention, use **Settings > Operation recovery**. Safe retries are rechecked against the files on disk. Dismissing an ambiguous operation never changes either file and marks the affected index for rebuilding.

After removing the executable, local state can be removed manually from:

```text
%LOCALAPPDATA%\VrcImageCurator
```

If **Start with Windows** was enabled, disable it in Settings before removal. Its per-user registration is stored at:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
```

under the value `VrcImageCurator`.

## Build

Requires the .NET 10 SDK on Windows.

```powershell
dotnet restore VrcImageCurator.sln
dotnet test VrcImageCurator.sln -c Release
dotnet publish src\VrcImageCurator.App\VrcImageCurator.App.csproj -p:PublishProfile=Portable
```

The portable output is written to `publish`.

For isolated verification, `--data-dir C:\absolute\temporary\folder` redirects state, holding files, logs, and default image folders beneath that location. Windows startup registration changes are disabled in this mode.

## License

VRC Image Curator is released under the MIT License. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
