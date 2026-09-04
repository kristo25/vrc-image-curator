# VRC Image Curator

VRC Image Curator is a local Windows tool for reviewing duplicate and similar images collected by VRCX. It scans the fixed `Emoji`, `Prints`, and `Stickers` categories, keeps unique images organized, and places possible matches in a persistent visual review queue.

## Safety model

- Filenames and metadata do not determine image identity.
- Exact matches use normalized decoded pixels, dimensions, GIF frame order, and frame timing.
- Similar matches are always reviewed by the user.
- Images are processed in path order. Each unique image moves into the archive immediately, so later files in the same scan are compared against the updated archive.
- Possible matches remain in their incoming folders while the Review queue stores references to them; scanning does not create or move a review copy.
- Clearing the Review queue leaves those incoming files untouched. Reviews created by older versions are still returned safely from the legacy holding folder.
- `Keep incoming` and `Keep match` use the Windows Recycle Bin for the image that is not kept. Permanent deletion is never used as a fallback.
- Completed operations and resolved reviews are compacted automatically; the activity history retains the newest 1,000 entries.
- Oversized or unusually frame-heavy images are rejected before full decoding to protect application memory.
- Interrupted moves and recycle requests are recorded in a durable operation journal and reconciled on restart.
- Archive fingerprints are stored locally and reused when a file's path, size, and modification time are unchanged. Every scan refreshes additions, removals, and changed files before matching.
- Settings save automatically when a field is committed. A folder that does not exist yet is reported inline rather than refused, so a folder that appears later still works; only overlapping source and output folders block a save.
- Tests use temporary folders and never access the configured VRCX or archive folders.

## Supported files

PNG, animated GIF, JPG/JPEG, WebP, and BMP are supported. MP4, `.temp`, and unsupported files stay untouched.

## Getting started

1. Run `VrcImageCurator.exe`.
2. Open **Settings**.
3. Choose the source folders and enable the categories you want to use.
4. Choose one main output folder. The default is `Images\VRChat\Archived Images` beneath your user folder.
5. Settings save automatically as you change them. Use **Create missing folders** if a configured folder does not exist yet, then select **Scan now**.
6. Use **Scan another folder** for a one-time recursive scan outside the configured VRCX folders.
7. Use **Start watching** when you want the app to monitor configured folders during the current session. Temporarily unavailable folders are attached automatically when they return.
8. Review matches with **Keep incoming**, **Keep match**, or **Move as Unique**.

Suggested VRCX source root:

```text
C:\Users\<you>\Images\VRChat
```

The default category folders are `Emoji`, `Prints`, and `Stickers` beneath that root, and the default archive is `Archived Images` beside them.

The app suggests category folders beneath that root. Source paths remain editable. New files are written beneath the single output root while preserving their category-relative path. For example, `Emoji\2025-05\image.png` moves to `<output>\Emoji\2025-05\image.png`.

**Start with Windows** launches the app in background watching mode. Ordinary launches begin with watching stopped.

## Portable installation and removal

No installer is required. Keep the executable anywhere you can write and run it.

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
