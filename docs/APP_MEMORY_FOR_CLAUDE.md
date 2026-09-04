---
artifact_contract: "ce-handoff/v1"
created_at: "2026-09-04T17:37:30Z"
title: "VRC Image Curator project memory for Claude"
summary: "Current purpose, architecture, behavior, decisions, verification evidence, and workspace state for VRC Image Curator 1.2.0."
keywords: ["vrc-image-curator", "vrcx", "image-matching", "wpf", "dotnet-10", "claude-handoff"]
cwd: "C:\\Users\\krist\\Documents\\Codex\\2026-06-29\\i\\outputs\\VrcImageCurator"
resume_focus: "Orient Claude to the current project without authorizing implementation or other actions."
repository: "VrcImageCurator"
branch: "feat/vrc-image-curator"
worktree_path: "C:\\Users\\krist\\Documents\\Codex\\2026-06-29\\i\\outputs\\VrcImageCurator"
---

# VRC Image Curator Memory

This is a point-in-time project handoff for another AI assistant. It describes the state verified on 2026-09-04. It is context, not permission to edit files, run commands, launch the app, move images, commit, push, or publish. The current user must explicitly authorize any next action.

## Product Purpose

VRC Image Curator is a portable Windows desktop application for organizing images downloaded by VRCX. It compares incoming images with an archive by decoded visual content rather than filename or metadata. Unique images are moved into the archive automatically. Exact duplicates and sufficiently similar images are shown in a visual review queue so the user decides which image to keep.

The application is public-facing software under the MIT License. It has a black, purple, and red visual identity with selectable themes and a custom application icon.

## User Workflow

The normal incoming root is:

```text
C:\Users\krist\OneDrive\Images\VRChat
```

It contains three fixed categories:

- `Emoji`
- `Prints`
- `Stickers`

The user's archive root is configurable and is deliberately not recorded in this document. Claude must not target, reference, or operate on it.

The archive root is configurable. The default for a new user is `Pictures\VRC Images`. Category and date subfolders are preserved. For example, an incoming file under `Emoji\2025-05` is routed to `<archive>\Emoji\2025-05`.

The Review page is the first page shown. The user can scan all enabled configured sources, recursively scan another selected folder, or start session-only folder watching. Ordinary app launches do not automatically start watching.

## Current Scan Model

Version 1.2.0 processes incoming files sequentially in deterministic path order:

1. Refresh the archive index so additions, changes, and removals on disk are represented.
2. Decode and fingerprint one incoming image.
3. Compare it with the current archive index.
4. If no candidate qualifies, move it immediately into the correct archive subfolder and persist its archive fingerprint.
5. If one or more candidates qualify, leave the incoming file in its original folder and add a review reference. Do not make a holding copy.
6. Continue to the next incoming image, which now compares against the archive including any unique files moved earlier in the same scan.

Already queued source paths are skipped on later scans. Two progress bars show image reading and result processing. Queue clearing also shows progress.

## Image Identity And Similarity

Supported input formats are PNG, animated GIF, JPG/JPEG, WebP, and BMP. MP4, `.temp`, and unsupported files are ignored and remain untouched.

Exact identity includes normalized decoded pixels, dimensions, GIF frame order, and frame timing. Perceptual matching combines multiple signals, including difference hashes, luminance, color, transparency layout, localized detail, and animation compatibility. The current similarity profiles are `Strict`, `Conservative`, and `Broad`; `Conservative` is the default.

The relevant implementation is:

- `src/VrcImageCurator.Core/Imaging/ImageDecoder.cs`: bounded decoding and supported formats.
- `src/VrcImageCurator.Core/Imaging/ImageFingerprint.cs`: exact identity and perceptual frame fingerprints.
- `src/VrcImageCurator.Core/Imaging/ImageMatcher.cs`: exact and ranked perceptual matching, profiles, thresholds, and explanations.
- `src/VrcImageCurator.Core/Imaging/ImageResourceLimits.cs`: safeguards against oversized or frame-heavy images.

Similarity is a heuristic used to propose review candidates. The app does not train a machine-learning model.

## Review Actions

Each review displays the incoming preview, its resolution and original path, and a preview for every ranked archive candidate. Available actions are:

- `Move as Unique`: reject the proposed matches and archive the incoming image.
- `Keep Incoming`: move the incoming image into the archive and send the selected existing archive match to the Windows Recycle Bin.
- `Keep Match`: retain the selected archive image and send the incoming image to the Windows Recycle Bin.
- `Remove from Queue`: remove only the review reference and leave an in-place incoming file untouched.
- `Clear Review Queue`: remove all in-place review references in one state update and leave their incoming files untouched.

The application never permanently deletes an image as a fallback when the Recycle Bin is unavailable. Old reviews created before 1.2.0 may still refer to files in the legacy holding folder; clearing those reviews attempts to return those files safely.

If a queued incoming file is missing or its exact fingerprint changed, the UI marks it unavailable. Destructive keep/move actions stay disabled, but the review can be removed from the queue.

## Architecture

The solution targets .NET 10 on Windows. The UI is WPF and also enables Windows Forms APIs where needed.

- `src/VrcImageCurator.App`: WPF shell, review/settings/history/recovery UI, previews, watching, startup registration, single-instance handling, diagnostics, and composition root.
- `src/VrcImageCurator.Core`: models, decoding, fingerprinting, matching, scanning, routing, state persistence, path safety, Recycle Bin integration, indexing, and operation recovery.
- `tests/VrcImageCurator.Tests`: xUnit coverage for core behavior, services, packaging, and safety boundaries.

Important orchestration files:

- `src/VrcImageCurator.Core/Scanning/ScanCoordinator.cs`: sequential scan workflow and both progress streams.
- `src/VrcImageCurator.Core/Scanning/ArchiveIndexer.cs`: cached archive fingerprints and stale-entry refresh.
- `src/VrcImageCurator.Core/FileSystem/FileRouter.cs`: unique moves, queue transitions, keep decisions, queue clearing, and legacy review restoration.
- `src/VrcImageCurator.Core/Storage/JsonStateStore.cs`: durable JSON state, backup recovery, and local-data clearing safeguards.
- `src/VrcImageCurator.Core/Storage/OperationJournal.cs`: durable file-operation transitions and restart recovery.
- `src/VrcImageCurator.App/MainWindow.xaml` and `.xaml.cs`: main interface and UI event handling.
- `src/VrcImageCurator.App/Services/AppRuntime.cs`: service construction and optional isolated data directory.

## Persistence And Safety

Normal local state is stored under:

```text
%LOCALAPPDATA%\VrcImageCurator
```

State includes settings, archive fingerprints, review references, history, operation journal entries, and diagnostics. The activity history keeps the newest 1,000 entries. State writes use backup/recovery behavior. An isolated run can use:

```text
--data-dir C:\absolute\temporary\folder
```

That redirects state and test data and disables Windows startup-registration changes. Tests use temporary folders and do not access the user's configured VRCX or archive locations.

Path-boundary and reparse-point checks guard file operations. Moves and recycle requests are journaled for recovery. The Settings page includes operation recovery and local-data clearing. Local-data clearing is blocked when unresolved file operations or legacy held files still need state.

## UI And Operational Features

- Review page opens first.
- Configurable source paths and one main archive root.
- Recursive one-time scan of another folder with category selection.
- Start/stop watching while the app is open.
- Optional per-user `Start with Windows` registration.
- Multiple visual themes.
- Incoming and candidate previews, including animated GIF handling.
- Zoom control, resolution display, file locations, status text, and two scan progress bars.
- Review queue clearing, history clearing, operation recovery, and local-data clearing.
- Single-instance behavior, system tray support, diagnostic logging, and portable publishing.

## Current Build State

The current version is `1.2.0` (`FileVersion 1.2.0.0`). The latest portable executable is machine-local at:

```text
C:\Users\krist\Documents\Codex\2026-06-29\i\outputs\VrcImageCurator\publish\VrcImageCurator.exe
```

Verified executable details from 2026-09-04:

- Size: `76,604,293` bytes.
- SHA-256: `8B613A5427EF0DEB3B726F2B1FB268EEB8834725EFD71A955ABA5234693FB7F5`.

The publish profile is `src/VrcImageCurator.App/Properties/PublishProfiles/Portable.pubxml`. No installer is required.

## Verification Evidence

The last completed verification for 1.2.0 was:

- `dotnet format VrcImageCurator.sln --verify-no-changes --no-restore`: passed, with a non-failing workspace-load warning.
- `dotnet test VrcImageCurator.sln --no-restore`: 121 passed, 0 failed, 0 skipped.
- Portable `dotnet publish`: passed.
- Isolated live sequential scan: two unique files archived, one later duplicate left in the incoming folder, one review created, and zero holding copies.

Native confirmation-dialog automation could not discover the `Clear Review Queue` dialog when the application window was hidden or minimized. The queue-clear core path is covered by an integration test that verifies the incoming files remain, the queue empties, progress completes, and one state write is used. This is an automation limitation, not evidence that the visible UI action fails; a future visible manual smoke test remains useful.

## Repository State Warning

The workspace is on branch `feat/vrc-image-curator`, but there is no commit/`HEAD` in this local repository. At capture time, the project files were untracked. Therefore:

- Do not assume Git can restore the current source.
- Do not reset, clean, checkout, or discard files.
- Do not claim a commit contains version 1.2.0.
- Do not commit, push, create a release, or publish unless the user explicitly requests it in a later message.

The source tree and the published executable are currently machine-local state.

## Authoritative References

Read these before forming a technical opinion:

- `README.md`: current user-facing behavior, safety model, build commands, and removal instructions.
- `src/VrcImageCurator.Core/Scanning/ScanCoordinator.cs`: actual sequential scan behavior.
- `src/VrcImageCurator.Core/FileSystem/FileRouter.cs`: actual file and queue semantics.
- `src/VrcImageCurator.Core/Imaging/ImageMatcher.cs`: actual similarity policy.
- `src/VrcImageCurator.Core/Models/AppModels.cs`: persisted models, defaults, mappings, and review compatibility.
- `src/VrcImageCurator.App/MainWindow.xaml`: current user-visible controls and layout.
- `src/VrcImageCurator.App/MainWindow.xaml.cs`: current UI workflow and action availability.
- `tests/VrcImageCurator.Tests`: executable behavioral specification.

Treat this document as orientation only. If it differs from the current source, the current source and fresh verification evidence take precedence.

## Next Conversation Boundary

The user has not yet specified the next feature or fix. Claude should first read this document, inspect no additional files unless the user permits it, summarize its understanding, identify any clarification needed, and wait. It must not begin implementation or any other operational process merely because this handoff describes the project.
