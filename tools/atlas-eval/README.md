# Atlas detection evaluation

A dev tool, not part of the application build. It answers one question before
any of the detector is ported to C#: **how often does automatic grid detection
actually get the grid right on real sheets?**

That question needs measuring rather than assuming, because two things are true
of the detector as it stands:

- **Confidence does not predict correctness.** On a trial corpus the highest
  confidence of the whole run, 0.784, was a meaningless 2x4 grid found in an
  image of random noise.
- **It has no "not an atlas" answer.** Given any image it returns a grid. A
  single sprite came back as 8x8, a colour gradient as 11x8. Nothing in the
  detector declines.

So a cheap pre-screen is not an optimisation, it is the gate that keeps
ordinary images out of the queue. This tool reports what that screen would have
said alongside what detection found, so both can be calibrated on the same real
images.

## Running it

Needs `numpy` and `Pillow`, plus a checkout of VRCAtlasReanimator for the
detector itself (its code is not vendored here).

```
python evaluate.py \
    --atlas-src /path/to/VRCAtlasReanimator \
    --images "%USERPROFILE%\Pictures\VRChat Archive\Emoji" \
    --out ./report
```

## Reading the result

`report/index.html` is a contact sheet: every image with the detected grid drawn
over it in red, its confidence, its screen score, and the time both took. Tick
every card whose grid is wrong. That count is the hit rate, and it decides
whether the detector is worth porting or whether Curator should hand atlases off
to the standalone app instead.

`report/results.csv` has the same data per image, including what each detection
pipeline found separately, for calibrating `SCREEN_THRESHOLD`.

## Measured result (191 real emoji, 2026-09-11)

VRChat names its emoji exports `..._<n>frames_<fps>fps_<loop>loopStyle.png`, so the
frame count and playback rate are already in the filename. That gave free ground
truth for 56 sheets, with the remaining 135 files labelled "not a sheet" by the
absence of the pattern.

| | result |
|---|---|
| grid detector, exact frame count | **43/56 (77%)** |
| filename + fixed layout rule | **54/56 (96%)** |
| detection cost | 178 ms median, 535 ms worst |
| screen cost | 1.9 ms median |

Every sheet in the sample is 1024x1024, and the layout follows one rule:

```
columns = 4 if frames <= 16 else 8
rows    = ceil(frames / columns)
```

That rule reproduces **all 43** grids the detector got right, and fixes all 13 it
got wrong. Two sheets out of 56 carry content past the last named frame; the
detector was also wrong on both.

Two further results worth recording:

- **Confidence does not predict correctness.** Median confidence was 0.639 on
  correct detections and 0.637 on wrong ones. It cannot gate anything.
- **The cheap screen does not separate real images.** Sheets scored a median of
  0.717 and non-sheets 0.244, but the distributions overlap badly: a threshold of
  0.25 admits 63 of 135 non-sheets, and 0.35 loses 12 of 56 sheets. For VRChat
  emoji the filename is a better gate than any pixel heuristic.
