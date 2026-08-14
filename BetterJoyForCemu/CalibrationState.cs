using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterJoyForCemu {
    // Calibration sample buffers and stored per-controller calibration data, extracted out of
    // MainForm so Joycon.cs/Program.cs don't need a live MainForm (or any UI) to read/write them -
    // matters for running headless (see BetterJoyService), where there's no MainForm at all.
    public static class CalibrationState {
        public static bool Calibrating = false;

        // Which specific controller Calibrating applies to - the sample lists below are shared/
        // global, so without this, calibrating one controller while others stay connected would
        // let every other connected controller's Poll() thread ALSO dump samples into the same
        // buffers (each one calls AddSample on every packet whenever Calibrating is true),
        // corrupting the result with a mix of unrelated controllers' readings. This is what the
        // old "exactly one controller connected" UI restriction was actually working around -
        // scoping admission to one specific controller makes that restriction unnecessary.
        public static Joycon CalibratingController = null;

        // Names the controller a calibration Start/Done prompt is currently showing for, so
        // Joycon.DoThingsWithButtons (running on that controller's own Poll thread) knows a face
        // button press right now means "confirm this prompt" rather than nothing. Set by
        // MainForm/HeadlessJoyconHost whenever they show a prompt, cleared by whichever one
        // actually acts on it (mouse click or button press - see each host's confirm handler) so
        // a stray press after the prompt's already been dismissed does nothing.
        public static Joycon PendingConfirmController = null;

        public static List<int> XG = new List<int>(), YG = new List<int>(), ZG = new List<int>();
        public static List<int> XA = new List<int>(), YA = new List<int>(), ZA = new List<int>();
        public static List<KeyValuePair<string, float[]>> CaliData = new List<KeyValuePair<string, float[]>> {
            new KeyValuePair<string, float[]>("0", new float[6] { 0, 0, 0, -710, 0, 0 })
        };

        // Stick recalibration is deliberately a separate lifecycle from the gyro/accel one
        // above (own Calibrating/CalibratingController-equivalent flags, own phase, own
        // sample buffers) rather than reusing Calibrating/CalibratingController - it runs as
        // as a second phase tacked onto the end of the same UI flow (see MainForm/
        // HeadlessJoyconHost), and FinishCalibration already clears Calibrating/
        // CalibratingController as soon as the gyro phase ends, which would otherwise stop
        // stick sample admission before the stick phase even started.
        public static bool StickCalibrating = false;
        public static Joycon StickCalibratingController = null;

        public enum StickPhase { None, Center, Range }
        public static StickPhase CurrentStickPhase = StickPhase.None;

        // Which physical stick the current step is actually asking the user to move - the UI
        // walks sticks one at a time (see MainForm's step sequence), but Joycon.cs still feeds
        // BOTH sticks' samples unconditionally every packet on a Pro controller. Without this
        // gate, incidental movement/jitter on the stick NOT currently being calibrated (e.g. a
        // resting thumb) would leak into that stick's accumulators and get baked into its
        // calibration by a later FinishStickCalibration call for it, despite the user never
        // having been prompted to move it yet.
        public enum StickTarget { Primary, Secondary }
        public static StickTarget CurrentStickTarget = StickTarget.Primary;

        // No default/fallback entry (unlike CaliData) - absence means "no empirical override
        // exists for this controller," and callers (Joycon.getActiveStickData) fall back to
        // the controller's own SPI-read factory/user calibration, not a hardcoded stand-in.
        // Stick2CaliData only ever gets entries for Pro controllers - a plain Joycon has one
        // physical stick, so its stick2 samples are never fed in the first place.
        public static List<KeyValuePair<string, ushort[]>> StickCaliData = new List<KeyValuePair<string, ushort[]>>();
        public static List<KeyValuePair<string, ushort[]>> Stick2CaliData = new List<KeyValuePair<string, ushort[]>>();

        private static List<int> stickCenterX = new List<int>(), stickCenterY = new List<int>();
        private static List<int> stick2CenterX = new List<int>(), stick2CenterY = new List<int>();
        private static StickRangeTracker stickRange = new StickRangeTracker();
        private static StickRangeTracker stick2Range = new StickRangeTracker();

        // Running min/max of raw stick position seen during the "move it to its extremes"
        // phase - cheaper than keeping every sample just to take a min/max at the end, and
        // there's no need for a median here the way there is for center (a single stray
        // outlier reading during deliberate full-range movement is a real edge the user
        // reached, not noise to filter out).
        private class StickRangeTracker {
            public bool Seeded = false;
            public int MinX, MaxX, MinY, MaxY;

            public void Update(int x, int y) {
                if (!Seeded) {
                    MinX = MaxX = x;
                    MinY = MaxY = y;
                    Seeded = true;
                    return;
                }
                if (x < MinX) MinX = x;
                if (x > MaxX) MaxX = x;
                if (y < MinY) MinY = y;
                if (y > MaxY) MaxY = y;
            }
        }

        // Guards the sample lists between a Joycon's own packet-processing thread (AddSample,
        // called for every packet while a calibration window is open) and FinishCalibration -
        // without this, FinishCalibration reading/enumerating a list while AddSample is
        // concurrently appending to it could throw or compute from a half-collected set.
        private static readonly object samplesLock = new object();

        public static void ClearSamples() {
            lock (samplesLock) {
                XG.Clear(); YG.Clear(); ZG.Clear();
                XA.Clear(); YA.Clear(); ZA.Clear();
            }
        }

        public static void ClearStickSamples() {
            lock (samplesLock) {
                stickCenterX.Clear(); stickCenterY.Clear();
                stick2CenterX.Clear(); stick2CenterY.Clear();
                stickRange = new StickRangeTracker();
                stick2Range = new StickRangeTracker();
            }
        }

        // Called from a Joycon's own read thread once per axis per packet while Calibrating is
        // true - source identifies which controller the sample came from, so only the one
        // actually being calibrated (CalibratingController) is admitted; every other connected
        // controller's calls are silently ignored rather than contaminating the buffers. Both
        // the check and the appends happen under the same lock FinishCalibration uses to stop
        // admission and snapshot the lists, so a sample can never land in the narrow window
        // between "stop admitting" and "read for calculation."
        public static void AddSample(Joycon source, List<int> accList, List<int> gyroList, int accValue, int gyroValue) {
            lock (samplesLock) {
                if (!Calibrating || source != CalibratingController)
                    return;
                accList.Add(accValue);
                gyroList.Add(gyroValue);
            }
        }

        public static float[] ActiveCaliData(string serialNumber) {
            foreach (var entry in CaliData)
                if (entry.Key == serialNumber)
                    return entry.Value;
            return CaliData[0].Value;
        }

        private static int FindSerialIndex(string serialNumber) {
            for (int i = 0; i < CaliData.Count; i++)
                if (CaliData[i].Key == serialNumber)
                    return i;
            return -1;
        }

        // Called from a Joycon's own read thread once per packet (twice for a Pro controller -
        // once per physical stick) while StickCalibrating is true - same admission pattern as
        // AddSample above, just against the independent Stick* flags so this phase can keep
        // running after FinishCalibration has already cleared Calibrating/CalibratingController
        // for the gyro phase that preceded it.
        public static void AddStickSample(Joycon source, bool secondary, ushort x, ushort y) {
            lock (samplesLock) {
                if (!StickCalibrating || source != StickCalibratingController)
                    return;

                bool wantsSecondary = CurrentStickTarget == StickTarget.Secondary;
                if (secondary != wantsSecondary)
                    return;

                if (CurrentStickPhase == StickPhase.Center) {
                    if (secondary) { stick2CenterX.Add(x); stick2CenterY.Add(y); }
                    else { stickCenterX.Add(x); stickCenterY.Add(y); }
                } else if (CurrentStickPhase == StickPhase.Range) {
                    if (secondary) stick2Range.Update(x, y);
                    else stickRange.Update(x, y);
                }
            }
        }

        // Returns null - not a hardcoded default - when this controller has never had stick
        // recalibration run, so the caller keeps using its own SPI-read factory/user data.
        public static ushort[] ActiveStickCal(string serialNumber, bool secondary) {
            var data = secondary ? Stick2CaliData : StickCaliData;
            foreach (var entry in data)
                if (entry.Key == serialNumber)
                    return entry.Value;
            return null;
        }

        private static void StoreStickCal(List<KeyValuePair<string, ushort[]>> data, string serialNumber, ushort[] cal) {
            for (int i = 0; i < data.Count; i++) {
                if (data[i].Key == serialNumber) {
                    data[i] = new KeyValuePair<string, ushort[]>(serialNumber, cal);
                    return;
                }
            }
            data.Add(new KeyValuePair<string, ushort[]>(serialNumber, cal));
        }

        // Builds a stick_cal-shaped array ([xMaxAboveCenter, yMaxAboveCenter, xCenter, yCenter,
        // xMinBelowCenter, yMinBelowCenter], matching Joycon.cs's SPI-derived layout exactly so
        // CenterSticks needs no changes to consume either source) from the just-collected
        // samples, or null if this stick was never actually sampled (e.g. stick2 on a plain
        // Joycon, which only has one physical stick and never feeds stick2 samples at all).
        private static ushort[] ComputeStickCal(List<int> centerX, List<int> centerY, StickRangeTracker range) {
            if (centerX.Count == 0 || !range.Seeded)
                return null;

            Random rnd = new Random();
            int cx = (int)QuickselectMedian(centerX, rnd.Next);
            int cy = (int)QuickselectMedian(centerY, rnd.Next);

            // Clamped to at least 1 - a user who never actually moved the stick during the
            // range phase would otherwise leave max/min collapsed onto (or inside of) the
            // center, which CenterSticks would then divide by, clamping every future input
            // to zero instead of just being a wasted calibration attempt.
            int maxAboveX = Math.Max(range.MaxX - cx, 1);
            int minBelowX = Math.Max(cx - range.MinX, 1);
            int maxAboveY = Math.Max(range.MaxY - cy, 1);
            int minBelowY = Math.Max(cy - range.MinY, 1);

            return new ushort[] {
                (ushort)maxAboveX, (ushort)maxAboveY,
                (ushort)cx, (ushort)cy,
                (ushort)minBelowX, (ushort)minBelowY,
            };
        }

        // Mirrors FinishCalibration's shape (stop admission, snapshot under the lock, compute
        // outside it, publish once at the end) but for the independent stick lifecycle - see
        // the Stick* fields above for why this can't just reuse FinishCalibration's own
        // Calibrating/CalibratingController reset.
        //
        // Finalizes one stick at a time (matching CurrentStickTarget/the caller's step) rather
        // than both together - the UI walks left/right sticks as separate steps, so by the time
        // this runs for one of them, the other's accumulators are either empty (never touched
        // this step) or intentionally ignored, not published alongside data the user was never
        // actually prompted to provide.
        public static void FinishStickCalibration(string serialNumber, bool secondary) {
            List<int> centerX, centerY;
            StickRangeTracker range;
            lock (samplesLock) {
                StickCalibrating = false;
                StickCalibratingController = null;
                CurrentStickPhase = StickPhase.None;
                if (secondary) {
                    centerX = new List<int>(stick2CenterX);
                    centerY = new List<int>(stick2CenterY);
                    range = stick2Range;
                } else {
                    centerX = new List<int>(stickCenterX);
                    centerY = new List<int>(stickCenterY);
                    range = stickRange;
                }
            }

            ushort[] cal = ComputeStickCal(centerX, centerY, range);
            if (cal != null)
                StoreStickCal(secondary ? Stick2CaliData : StickCaliData, serialNumber, cal);

            Config.SaveStickCaliData(StickCaliData, Stick2CaliData);
        }

        // Computes each axis's median from the just-collected samples and stores it as the
        // active controller's calibration offsets, replacing any prior entry for the same
        // serial number. Stops admission and snapshots every list atomically (under the same
        // lock AddSample uses) before calculating, and only publishes to CaliData once all six
        // medians succeed - a failure partway through (e.g. an empty list) previously could
        // leave a half-computed entry in CaliData, since the target array was mutated in place
        // as each axis finished rather than published all at once at the end.
        public static void FinishCalibration(string serialNumber) {
            List<int> xg, yg, zg, xa, ya, za;
            lock (samplesLock) {
                Calibrating = false;
                CalibratingController = null;
                xg = new List<int>(XG);
                yg = new List<int>(YG);
                zg = new List<int>(ZG);
                xa = new List<int>(XA);
                ya = new List<int>(YA);
                za = new List<int>(ZA);
            }

            Random rnd = new Random();
            float[] arr = new float[6];
            arr[0] = (float)QuickselectMedian(xg, rnd.Next);
            arr[1] = (float)QuickselectMedian(yg, rnd.Next);
            arr[2] = (float)QuickselectMedian(zg, rnd.Next);
            arr[3] = (float)QuickselectMedian(xa, rnd.Next);
            arr[4] = (float)QuickselectMedian(ya, rnd.Next);
            arr[5] = (float)QuickselectMedian(za, rnd.Next) - 4010; // Joycon.cs acc_sen 16384

            int serIndex = FindSerialIndex(serialNumber);
            if (serIndex == -1)
                CaliData.Add(new KeyValuePair<string, float[]>(serialNumber, arr));
            else
                CaliData[serIndex] = new KeyValuePair<string, float[]>(serialNumber, arr);

            Config.SaveCaliData(CaliData);
        }

        private static double QuickselectMedian(List<int> l, Func<int, int> pivotFn) {
            int ll = l.Count;
            if (ll % 2 == 1)
                return Quickselect(l, ll / 2, pivotFn);
            return 0.5 * (Quickselect(l, ll / 2 - 1, pivotFn) + Quickselect(l, ll / 2, pivotFn));
        }

        private static int Quickselect(List<int> l, int k, Func<int, int> pivotFn) {
            if (l.Count == 1 && k == 0)
                return l[0];
            int pivot = l[pivotFn(l.Count)];
            List<int> lows = l.Where(x => x < pivot).ToList();
            List<int> highs = l.Where(x => x > pivot).ToList();
            List<int> pivots = l.Where(x => x == pivot).ToList();
            if (k < lows.Count)
                return Quickselect(lows, k, pivotFn);
            if (k < lows.Count + pivots.Count)
                return pivots[0];
            return Quickselect(highs, k - lows.Count - pivots.Count, pivotFn);
        }
    }
}
