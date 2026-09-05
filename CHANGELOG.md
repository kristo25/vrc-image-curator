# Changelog

All notable changes to VRC Image Curator are recorded here. This project follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
  `%LOCALAPPDATA%\VrcImageCurator\state.json` if you may want to return to 1.2.0.
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
