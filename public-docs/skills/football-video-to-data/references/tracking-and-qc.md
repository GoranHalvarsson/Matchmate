# Tracking and quality control

Use this branch only when the task actually needs estimated positions. Start with a small representative wide-view passage, a close-up/camera-cut control and independently chosen heldout landmarks. Set the budget, sample cadence, maximum unsupported gap and acceptance measures before inference. Sparse success does not qualify a long continuous clip; a failed single clip does not establish universal settings.

## Calibration before projection

PnLCalib is an optional football-specific candidate for point/line-based field calibration. Its upstream source/weights are external dependencies, not bundled or downloaded by the skill. Pin the selected code revision, configuration, checkpoint hashes and preprocessing. Review the actual source, package, weight and dataset licenses for the intended use before adopting or redistributing a stack; this skill grants no rights over them.

Project field lines back onto actual images. Evaluate independent visible line intersections and off-axis/centre-circle checks, native pixel residual distributions, orientation, degeneracy and close-up abstention. Record manual uncertainty. Reprojection error alone does not establish physical metre accuracy. If field dimensions are assumed, state them; do not turn a convenient 105 × 68 template into a measured venue dimension.

OpenCV's homography/perspective APIs help evaluate planar geometry, but a manually chosen homography still needs defensible correspondences and independent checks. Collinear points, small visible regions, line confusion and moving cameras can invalidate it. Abstain on unsupported frames; evaluate consecutive frames around pans/zooms/cuts instead of carrying one camera forward blindly.

## Detection and association

YOLO11m plus BoT-SORT is a candidate person/tracklet stack, not a calibrated football identity system. Use existing local weights; Ultralytics model-name shortcuts can download absent weights. Confirm a local path and installed package before invoking:

```sh
yolo track model=local-models/yolo11m.pt source=trial.mp4 tracker=botsort.yaml classes=0 save=False
```

Capture actual per-frame detections, association settings, source PTS and failures. For controlled exports use the installed version's Python `YOLO(local_weights).track(..., stream=True, persist=True, tracker='botsort.yaml')` API; write raw results and a checkpoint rather than relying on rendered video alone. This command is a tool example, not a bundled full tracking runner or guaranteed no-download dependency check. Review package/default ReID/model behavior for the selected version before execution.

Measure representative detection recall/precision, duplicate boxes, track ID switches/fragmentation and camera support separately. Generic person detections may include officials or spectators. Team classification and named player identity require their own evidence. A tracker ID is not a player ID. Restrict manual joins to cited exact frames/intervals; do not propagate a point identity through an unseen gap.

Ground positions use uncertain visible foot/contact proxies with calibration support, not person-box centres. Withhold clipped feet, occluded/horizontal poses, merged boxes and bad camera frames. A screen ball observation is separate from pitch-ground position: an airborne ball cannot be projected onto a ground plane as its true location. Ball height stays unknown without defensible separate evidence; keep screen-only and missing samples.

## Qualification and export

- Validate raw time/PTS, camera state, per-person foot support, identity scope and ball state independently.
- Inspect overlays through the passage and around the worst residuals/jumps; record the actual sample cadence and exact reviewed frames.
- Export exact supported instants plus explicit gaps. Never hold/interpolate extracted positions across unsupported times. A separately labelled retrospective interpolation may be requested, but must not enter observed evidence.
- Preserve a failed whole-interval gate even when useful qualified subsets are accepted. Reviewers should be able to locate every withheld interval.
- Freeze hashes and protected-file preservation; do not import raw models/media or ignored research into runtime accidentally.

Sparse calibration successes do not establish tracker continuity or whole-interval camera quality. Evaluate these gates separately and report failures even when a useful subset survives. The bundled helper implements no detector, tracker, calibration runner or automated visual reviewer; this reference describes optional work with separately provided tools.

Official upstream references to consult for the selected versions: [PnLCalib](https://github.com/mguti97/PnLCalib), [Ultralytics tracking](https://docs.ultralytics.com/modes/track), [YOLO11](https://docs.ultralytics.com/models/yolo11/), [OpenCV calibration](https://docs.opencv.org/4.x/d9/d0c/group__calib3d.html).
