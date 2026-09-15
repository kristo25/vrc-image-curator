# Changelog

All notable changes to VRC Pic Sorter are recorded here. This project follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.4.0] - 2026-09-14

### Added

- **Animated emoji.** VRChat writes an emoji sheet's frame count, rate and loop direction into its
  file name, and that is now what the animation follows: the sheet is exported as a GIF to the
  name's own numbers. Measured against 56 real sheets the name was right on 55, where the best
  reading of the pixels managed 43, so the name decides and the pixels only raise an eyebrow.
- **Animations tab.** Lists the sheets still waiting on a decision, with *Show exported* and
  *Show skipped* to bring back the ones already settled.
- **The sheet beside the preview.** Every cell the animation uses is outlined, the cell playing now
  is highlighted, and art the name does not count is marked. One measurement feeds both the
  outlines and the export, so the picture cannot disagree with what was written.
- **Per-sheet overrides.** Frame count, frames per second and loop direction can each be corrected
  before exporting, for the occasional sheet whose name is wrong.
- **Skipping.** A sheet not worth animating can be skipped and brought back later. A skip is
  remembered by the image's fingerprint rather than its path, so it survives a rename, a move and
  an index rebuild, and a skipped sheet is no longer decoded on every scan. *Clear queue* skips
  everything still waiting; *Export missing* exports everything that has no animation yet.
- **Gif Ref filing.** A sheet is filed under `Animated\Gif Ref` once its animation exists. A
  ready-made GIF arriving from a source folder is filed with the animations and deduplicated
  against them, and one that matches a sheet already held is compared against that sheet's own
  animation - generated for the comparison if it has not been made yet. The animation folder is
  indexed, so all of this is recognised again on the next scan.
- `tools/atlas-eval`, the harness used to decide whether a pixel-based frame detector was worth
  porting, together with what it recorded over 191 real emoji.
- **Retained archives**, a section in Settings listing the folders that held the archive before the
  output folder was changed, with the number of images and the size of each, and a button to move
  them into the current archive. They stay indexed either way, so duplicates are still found across
  them; this makes consolidating them a visible choice rather than one that only appears while the
  output folder is being changed.

### Changed

- **The application is now called VRC Pic Sorter**, and the executable is `VrcPicSorter.exe`. The
  old name described only half of what it does now that emoji sheets are animated as well as
  deduplicated.
- **Default archive layout** is now `CategoryRoot`: everything for a category lands directly in
  that category's folder. The layout is selectable in Settings. An existing installation keeps
  whatever it is already set to - only a new installation, or state carried across the settings
  migration, takes the new default.
- **Keep incoming** asks for confirmation once per review instead of once per match, so settling a
  review that holds several matches no longer means dismissing the same dialog repeatedly. A match
  on a drive without a Recycle Bin still asks, because moving it to the Replaced folder is a
  different thing to agree to.
- Scanning no longer reads and rewrites every archived fingerprint for each incoming image. The
  view of the index is built once and reused until the index itself changes.
- A sheet whose pixels disagree with its name is recorded in the activity history as information.
  It used to be counted among the scan's failures, which made a sheet that animated exactly as
  asked announce *Scan completed with warnings*.

### Fixed

- Combo boxes and lists drew themselves from the Windows theme rather than the application's own
  colours. On a light Windows theme that left the *Loop* dropdown white with unreadable text and
  the *Matches* panel a white block in the middle of a dark window. 1.3.0 fixed the rows inside
  those lists; this covers the controls around them.
- The note for a sheet with blank cells inside its named range claimed the cells the name does not
  reach were left out. Nothing was left out - the name reaches all of them, and the blank ones are
  exported and play as empty frames. The sentence about art being dropped is now kept for the case
  where art really does sit past the last named frame.
- Filing a reviewed image follows the output folder that is set now, rather than the one recorded
  when the review was queued.
- Two copies of one picture each opened their own review card. An image waiting in Review is not in
  the archive, so nothing compared a later file against it, and a folder holding `emoji.gif` and
  `emoji (2).gif` asked the same question twice. A copy that decodes to exactly the same picture as
  one already waiting now goes to the Recycle Bin instead, the way a copy of an archived image
  already did. Without a Recycle Bin nothing is deleted and both copies still get asked about.
- **A drive with the Recycle Bin switched off was still recycled to.** Windows lets a drive be set
  to "remove files immediately when deleted", and then deletes permanently while still reporting a
  successful recycle - so the automatic duplicate handling, which asks nobody, could destroy the
  only copy of a picture. Such a drive is now treated as having no Recycle Bin at all, which every
  caller already answers by asking instead of deleting.
- A whole drive chosen as the main output folder matched nothing inside itself, because a volume
  root keeps its trailing separator. That quietly switched off the guards that stop the app
  scanning its own archive, and made every archive destination look like an escape from the folder
  it was already in.
- An animation the scan writes for a sheet already in the archive is now indexed by that same scan.
  It was written after the index was built, so for the rest of that scan it did not exist, and an
  incoming copy of it was archived all over again as though the app had never made it.
- An archive file deleted or locked between the moment a scan lists it and the moment it is read no
  longer disables the whole category. It is skipped and reported like any other unreadable file.
- Startup recovery no longer stops at the first pending operation whose drive cannot recycle. That
  one is marked as needing attention and the rest are still reconciled.
- Fingerprints held in memory during a scan survive the sidecar file disappearing underneath them.
  Reading state back mid-scan used to answer from the file alone, which stripped the fingerprints
  off everything the scan had just added and failed the rest of the run.
- Stop now cancels the scan that is actually running. With two categories queued it cancelled the
  one that had not started yet, while the running one carried on and the window said it was
  stopping.
- The Animations tab worked the exported frame rate out a second time instead of asking the
  exporter, and truncated where the exporter rounds: an 8 fps sheet was announced as "exported at
  7" and then exported at 8. Seven rates disagreed.
- Previews, fingerprint checks and the retained-archive scan no longer run on the thread drawing
  the window. One 4K screenshot froze it twice per click in the review queue, and a large archive
  froze the Settings page on every field.
- A preview whose decode finished after its card had left the screen left an animation timer
  running for the life of the window, holding every decoded frame with it.

### Known limitations

- **The frame count comes from the file name.** A sheet whose name is wrong exports blank frames,
  or leaves art out, and has to be corrected by hand on the Animations tab. The export says which
  of the two happened.
- Application state is a single JSON document that is rewritten on every file operation. It still
  grows with the archive, so a very large archive will see state writes dominate a scan.
- The archive index is still read from disk at the start of every scan; it is reused within a scan
  but not held between them.
- The published executable is not code-signed, so Windows SmartScreen warns on first run.

### Upgrading

The state document format is unchanged. 1.4.0 reads a 1.3.0 state as it stands, and the list of
skipped sheets is simply empty until the first one is skipped.

The rename is carried across for you the first time 1.4.0 runs:

- `%LOCALAPPDATA%\VrcImageCurator` is moved to `%LOCALAPPDATA%\VrcPicSorter`, so settings, the
  archive index, the review queue and any held incoming files come with you. The move happens only
  when there is no new folder already; nothing is ever merged or deleted, so a carry-over that
  cannot complete leaves you starting fresh with the old folder still on disk.
- A **Start with Windows** registration is re-registered under the new name and the old entry is
  removed, so it neither turns itself off nor keeps launching the old executable.

Two things are not moved, deliberately. An archive folder named `VRC Image Curator Replaced` keeps
its name and its contents, because it holds your files and renaming it would move them without
being asked. And the old `VrcImageCurator.exe` is simply replaced by the new one - delete it
whenever you like.

## [1.3.0] - 2026-09-05

### Added

- **Automatic duplicate resolution.** An incoming image that matches an archived image 100% at the
  same resolution and frame count is resolved without a prompt: the archived copy is kept and the
  incoming copy goes to the Recycle Bin. Different resolutions, and anything below 100%, still go
  to the review queue. See *Automatic duplicate resolution* in the README.
- **Stop button.** Scans, index rebuilds, operation recovery and queue clearing can now be
  interrupted. Cancellation happens between images, never during one.
- **Watch modes.** Folder watching either analyzes each image as it arrives (`OnDetection`,
  the default) or ignores individual arrivals and sweeps on a timer (`OnInterval`, default 60s).
- **Scan progress descriptions.** Both progress bars now name the current activity and file
  instead of showing bare counts.
- **Arrival notice.** The status bar reports `New entry detected` as soon as the watcher sees a
  file, before any analysis starts.
- Continuous integration on Windows running format verification, build, tests and publish.

### Changed

- **Settings save automatically** when a field is committed. The *Save settings* button is gone.
  Only overlapping source and output folders block a save; a folder that does not exist yet is
  reported inline, because a watched folder is allowed to appear later.
- **Default folders** are now `Pictures\VRChat\{Emoji,Prints,Stickers}` with the archive at
  `Pictures\VRChat\Archived Images`. The Pictures folder is resolved through Windows, so a
  Pictures folder redirected to OneDrive is found correctly.
- Watching analyzes only images that arrive while it is running; a full sweep happens on demand,
  on a timer in `OnInterval` mode, or when a watcher error or a returning folder means events
  were lost.
- Scanning creates its own output folder when missing. Source folders are never created.
- Similarity percentages are reported to one decimal place, so a match shown as 100% really is
  100% rather than something rounded up from 99.5%.
- *Keep match* no longer asks for confirmation.
- Activity history shows local time instead of UTC.
- A review action re-verifies only the candidate it will touch rather than every candidate.
- **State format.** Perceptual fingerprints have moved out of `state.json` into a
  `fingerprints.json` sidecar that is rewritten only when a fingerprint actually changes, so a
  routine file operation no longer rewrites megabytes of derived data. The state document is
  upgraded to schema 5 the first time 1.3.0 runs. **The upgrade is one way**: keep a copy of
  `%LOCALAPPDATA%\VrcPicSorter\state.json` if you may want to return to 1.2.0.
- Reading the current state revision no longer parses the whole document, and a completed move
  records three state writes instead of five.

### Fixed

- A single unreadable file in the archive no longer disables scanning for that whole category.
  Undecodable files are skipped with a warning and the index stays current.
- Unexpected errors no longer terminate the application. Dispatcher, domain and unobserved-task
  exceptions are reported and recorded.
- The scan settle check no longer opens incoming files exclusively, so it cannot block VRCX from
  writing into a folder being scanned.
- A running watcher is restarted when settings change, so folder, mode and interval changes take
  effect immediately.
- Stop now also cancels a scan started by folder watching.
- Automatic resolution falls back to review on any failure, not only when the Recycle Bin is
  unavailable.
- A watched folder that disappears and returns is swept again.
- List rows use the application's own colours instead of the Windows theme's chrome, which could
  paint a light background behind the Matches panel in any theme.
- Progress bar layout no longer shifts with the length of the current file name.
- `Keep incoming` no longer resolves the review before recycling the archived match. If that
  second step fails, the review stays in the queue so the decision can be retried.

### Known limitations

- Application state is a single JSON document that is rewritten on every file operation. Moving
  fingerprints into a sidecar removed the bulk of that cost, but the remaining document still
  grows with the archive, so a very large archive will still see state writes dominate a scan.
- The archive index and its fingerprints are re-read from disk for each scan rather than held in
  memory between scans.
- The published executable is not code-signed, so Windows SmartScreen warns on first run.

## [1.2.0]

First recorded release. Sequential scanning with in-place review references, durable operation
journal, Recycle Bin only deletion, and portable single-file publishing.
