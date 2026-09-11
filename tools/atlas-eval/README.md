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
