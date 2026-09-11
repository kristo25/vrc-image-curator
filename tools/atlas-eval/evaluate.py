#!/usr/bin/env python3
"""Measure how well atlas grid detection actually works, before porting it.

Runs the VRCAtlasReanimator detector over a folder of images and reports, per
image: what grid each pipeline found, how confident it was, how long it took,
and what a cheap pre-screen would have said. Writes a CSV plus an HTML contact
sheet of grid overlays so a human can mark which detections are wrong - that
labelling is the point, because detection confidence does not predict
correctness and there is no other source of ground truth.

    python evaluate.py --atlas-src ../VRCAtlasReanimator --images <folder> --out <folder>

Dev tool. Not part of the application build.
"""
from __future__ import annotations

import argparse
import csv
import html
import math
import sys
import time
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

SCREEN_WORKING_EDGE = 512
SCREEN_MIN_PERIOD = 8
# Above this the screen calls an image a possible atlas. Calibrate it on real
# images before trusting it: the detector itself has no "not an atlas" answer,
# so this threshold is the only thing keeping non-atlases out of the queue.
SCREEN_THRESHOLD = 0.25
ALPHA_THRESHOLD = 12
COLOR_TOLERANCE = 22


def occupancy(rgba: np.ndarray) -> np.ndarray:
    """Content mask: alpha when it carries information, else distance from the
    border colour. Mirrors what the real detector does, so the screen's view of
    an image matches the detector's."""
    alpha = rgba[:, :, 3]
    lo, hi = int(alpha.min()), int(alpha.max())
    if lo < 250 and hi > 5 and lo != hi:
        return alpha > ALPHA_THRESHOLD
    rgb = rgba[:, :, :3]
    h, w, _ = rgb.shape
    b = max(1, min(3, h // 2, w // 2))
    ring = np.concatenate([
        rgb[:b, :, :].reshape(-1, 3), rgb[h - b:, :, :].reshape(-1, 3),
        rgb[:, :b, :].reshape(-1, 3), rgb[:, w - b:, :].reshape(-1, 3),
    ], axis=0)
    bg = np.median(ring, axis=0)
    return np.abs(rgb.astype(np.int16) - bg.astype(np.int16)).max(axis=2) > COLOR_TOLERANCE


def periodicity(profile: np.ndarray) -> tuple[float, int]:
    """Strength and period of the best repeating signal in a density profile.
    Returns (0.0, 0) when the profile is too short or flat to say anything."""
    n = profile.size
    if n < SCREEN_MIN_PERIOD * 2:
        return 0.0, 0
    window = max(3, n // 16)
    kernel = np.ones(window) / window
    detrended = profile - np.convolve(profile, kernel, mode="same")
    detrended -= detrended.mean()
    norm = float(np.dot(detrended, detrended))
    if norm < 1e-12:
        return 0.0, 0
    full = np.correlate(detrended, detrended, mode="full")[n - 1:] / norm
    upper = max(SCREEN_MIN_PERIOD + 1, n // 2)
    window_slice = full[SCREEN_MIN_PERIOD:upper]
    if window_slice.size == 0:
        return 0.0, 0
    best = int(np.argmax(window_slice))
    return float(window_slice[best]), best + SCREEN_MIN_PERIOD


def screen(image: Image.Image) -> dict:
    """The cheap pre-screen: is this worth handing to the real detector?
    Deliberately generous - a false positive costs a queue entry, a false
    negative means an atlas is never found at all."""
    started = time.perf_counter()
    scale = max(image.width, image.height) / SCREEN_WORKING_EDGE
    if scale > 1:
        small = image.resize(
            (max(1, int(image.width / scale)), max(1, int(image.height / scale))),
            Image.NEAREST,
        )
    else:
        small = image
    mask = occupancy(np.asarray(small.convert("RGBA")))
    col_score, col_period = periodicity(mask.mean(axis=0).astype(np.float64))
    row_score, row_period = periodicity(mask.mean(axis=1).astype(np.float64))
    return {
        "screen_score": round(max(col_score, row_score), 4),
        "screen_col": round(col_score, 4),
        "screen_row": round(row_score, 4),
        "screen_col_period": col_period,
        "screen_row_period": row_period,
        "screen_ms": round((time.perf_counter() - started) * 1000, 2),
    }


def overlay(image: Image.Image, analysis, out_path: Path, max_edge: int = 420) -> None:
    canvas = image.convert("RGBA").copy()
    draw = ImageDraw.Draw(canvas)
    for frame in analysis.frames:
        draw.rectangle(
            [frame.x, frame.y, frame.x + frame.width - 1, frame.y + frame.height - 1],
            outline=(255, 40, 40, 255),
            width=max(1, min(canvas.width, canvas.height) // 200),
        )
    canvas.thumbnail((max_edge, max_edge), Image.NEAREST)
    canvas.convert("RGB").save(out_path, "PNG")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--atlas-src", required=True, help="VRCAtlasReanimator checkout")
    parser.add_argument("--images", required=True, help="folder of images to evaluate")
    parser.add_argument("--out", required=True, help="where to write the report")
    parser.add_argument("--limit", type=int, default=0, help="stop after N images")
    args = parser.parse_args()

    sys.path.insert(0, str(Path(args.atlas_src).resolve()))
    from atlas_animator.core import grid_detector
    from atlas_animator.core.atlas_loader import AtlasLoader
    from atlas_animator.core.frame_detector import DEFAULT_SENSITIVITY, FrameDetector

    out = Path(args.out)
    (out / "overlays").mkdir(parents=True, exist_ok=True)

    paths = sorted(
        p for p in Path(args.images).rglob("*")
        if p.suffix.lower() in {".png", ".jpg", ".jpeg", ".webp"}
    )
    if args.limit:
        paths = paths[: args.limit]

    rows = []
    for path in paths:
        row = {"file": path.name, "path": str(path)}
        try:
            with Image.open(path) as probe:
                row.update(screen(probe.convert("RGBA")))
                row["screen_verdict"] = (
                    "atlas" if row["screen_score"] >= SCREEN_THRESHOLD else "not-atlas")
                row["width"], row["height"] = probe.width, probe.height
        except Exception as error:  # noqa: BLE001 - a report must not stop on one bad file
            row["error"] = f"screen: {error}"
            rows.append(row)
            continue

        try:
            atlas = AtlasLoader.load(path)
            for name, fn in (("independent", grid_detector.detect), ("joint", grid_detector.detect_joint)):
                started = time.perf_counter()
                result = fn(atlas.array, diagnostics=[], sensitivity=DEFAULT_SENSITIVITY)
                row[f"{name}_grid"] = f"{result.columns}x{result.rows}"
                row[f"{name}_conf"] = round(result.detection_confidence, 3)
                row[f"{name}_ms"] = round((time.perf_counter() - started) * 1000, 1)
            started = time.perf_counter()
            best = FrameDetector.analyze_atlas(atlas)
            row["chosen_grid"] = f"{best.columns}x{best.rows}"
            row["chosen_cell"] = f"{best.frame_width}x{best.frame_height}"
            row["chosen_conf"] = round(best.detection_confidence, 3)
            row["frames"] = best.frame_count
            row["detect_ms"] = round((time.perf_counter() - started) * 1000, 1)
            row["warnings"] = "; ".join(best.warnings)
            name = f"{len(rows):04d}_{path.stem}.png"
            overlay(atlas.image, best, out / "overlays" / name)
            row["overlay"] = f"overlays/{name}"
        except Exception as error:  # noqa: BLE001
            row["error"] = f"detect: {error}"
        rows.append(row)
        print(f"{path.name}: {row.get('chosen_grid', row.get('error'))}", flush=True)

    fields = sorted({key for row in rows for key in row})
    with (out / "results.csv").open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)

    cards = []
    for row in rows:
        if "overlay" not in row:
            cards.append(f"<div class=card><b>{html.escape(row['file'])}</b>"
                         f"<p class=err>{html.escape(str(row.get('error', 'no overlay')))}</p></div>")
            continue
        cards.append(
            f"<div class=card><img src='{row['overlay']}' loading=lazy>"
            f"<b>{html.escape(row['file'])}</b>"
            f"<p>{row['chosen_grid']} &middot; cell {row['chosen_cell']} &middot; {row['frames']}f<br>"
            f"confidence {row['chosen_conf']} &middot; screen {row['screen_score']}"
            f" ({row['screen_verdict']})<br>"
            f"detect {row['detect_ms']} ms &middot; screen {row['screen_ms']} ms</p>"
            f"<label><input type=checkbox> wrong</label></div>"
        )
    (out / "index.html").write_text(
        "<!doctype html><meta charset=utf-8><title>Atlas detection review</title>"
        "<style>body{font:14px system-ui;background:#14161a;color:#e8e8ea;margin:24px}"
        ".grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));gap:16px}"
        ".card{background:#1e2128;border:1px solid #2c313a;border-radius:8px;padding:12px}"
        "img{width:100%;image-rendering:pixelated;background:#0a0b0e;border-radius:4px}"
        "p{color:#a6acb8;margin:6px 0}.err{color:#e07a7a}label{cursor:pointer}</style>"
        f"<h1>Atlas detection review</h1><p>{len(rows)} images. "
        "Tick every card whose red grid is wrong, then tell Claude the count.</p>"
        f"<div class=grid>{''.join(cards)}</div>",
        encoding="utf-8",
    )
    print(f"\nWrote {out/'results.csv'} and {out/'index.html'} ({len(rows)} images)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
