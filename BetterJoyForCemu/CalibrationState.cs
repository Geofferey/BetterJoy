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

        public static List<int> XG = new List<int>(), YG = new List<int>(), ZG = new List<int>();
        public static List<int> XA = new List<int>(), YA = new List<int>(), ZA = new List<int>();
        public static List<KeyValuePair<string, float[]>> CaliData = new List<KeyValuePair<string, float[]>> {
            new KeyValuePair<string, float[]>("0", new float[6] { 0, 0, 0, -710, 0, 0 })
        };

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
