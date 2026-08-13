using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterJoyForCemu {
    // Calibration sample buffers and stored per-controller calibration data, extracted out of
    // MainForm so Joycon.cs/Program.cs don't need a live MainForm (or any UI) to read/write them -
    // matters for running headless (see BetterJoyService), where there's no MainForm at all.
    public static class CalibrationState {
        public static bool Calibrating = false;
        public static List<int> XG = new List<int>(), YG = new List<int>(), ZG = new List<int>();
        public static List<int> XA = new List<int>(), YA = new List<int>(), ZA = new List<int>();
        public static List<KeyValuePair<string, float[]>> CaliData = new List<KeyValuePair<string, float[]>> {
            new KeyValuePair<string, float[]>("0", new float[6] { 0, 0, 0, -710, 0, 0 })
        };

        public static void ClearSamples() {
            XG.Clear(); YG.Clear(); ZG.Clear();
            XA.Clear(); YA.Clear(); ZA.Clear();
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
        // serial number.
        public static void FinishCalibration(string serialNumber) {
            int serIndex = FindSerialIndex(serialNumber);
            float[] arr = new float[6] { 0, 0, 0, 0, 0, 0 };
            if (serIndex == -1) {
                CaliData.Add(new KeyValuePair<string, float[]>(serialNumber, arr));
            } else {
                arr = CaliData[serIndex].Value;
            }

            Random rnd = new Random();
            arr[0] = (float)QuickselectMedian(XG, rnd.Next);
            arr[1] = (float)QuickselectMedian(YG, rnd.Next);
            arr[2] = (float)QuickselectMedian(ZG, rnd.Next);
            arr[3] = (float)QuickselectMedian(XA, rnd.Next);
            arr[4] = (float)QuickselectMedian(YA, rnd.Next);
            arr[5] = (float)QuickselectMedian(ZA, rnd.Next) - 4010; // Joycon.cs acc_sen 16384

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
