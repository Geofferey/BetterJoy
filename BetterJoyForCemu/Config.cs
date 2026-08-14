using System;
using System.Collections.Generic;
using System.IO;

namespace BetterJoyForCemu {
	public static class Config { // stores dynamic configuration, including
		static readonly string path;
		static Dictionary<string, string> variables = new Dictionary<string, string>();

		const int settingsNum = 11; // currently - ProgressiveScan, StartInTray + special buttons

        static Config() {
            path = Path.Combine(AppPaths.DataDir, "settings");
        }

		public static string GetDefaultValue(string s) {
			switch (s) {
				case "ProgressiveScan":
					return "1";
				case "capture":
					return "key_" + ((int)WindowsInput.Events.KeyCode.PrintScreen);
				case "reset_mouse":
					return "joy_" + ((int)Joycon.Button.STICK);
			}
			return "0";
		}

		// Helper function to count how many lines are in a file
		// https://www.dotnetperls.com/line-count
		static long CountLinesInFile(string f) {
			// Zero based count
			long count = -1;
			using (StreamReader r = new StreamReader(f)) {
				string line;
				while ((line = r.ReadLine()) != null) {
					count++;
				}
			}
			return count;
		}

		static readonly string[] DefaultKeys = { "ProgressiveScan", "StartInTray", "capture", "home", "sl_l", "sl_r", "sr_l", "sr_r", "shake", "reset_mouse", "active_gyro" };

		// Startup only - tolerant/best-effort (a malformed individual line is skipped, not
		// fatal) and destructive when the file looks stale (deletes and recreates on too few
		// lines). Safe here because nothing else is racing this file at process start. See
		// ReloadSettingsOnly for the live-reload path, which can't make either assumption.
		//
		// stickCaliData/stick2CaliData are the two lines following caliData - an install
		// upgrading from before stick recalibration existed simply won't have them, which the
		// line-count check below already tolerates (only settingsNum, i.e. the basic settings
		// block, is required); both lists are left empty in that case, which is exactly the
		// correct "no empirical override yet" state.
		public static void Init(List<KeyValuePair<string, float[]>> caliData, List<KeyValuePair<string, ushort[]>> stickCaliData, List<KeyValuePair<string, ushort[]>> stick2CaliData) {
			foreach (string s in DefaultKeys)
				variables[s] = GetDefaultValue(s);

			if (File.Exists(path)) {

				// Reset settings file if old settings
				if (CountLinesInFile(path) < settingsNum) {
					File.Delete(path);
					Init(caliData, stickCaliData, stick2CaliData);
					return;
				}

				using (StreamReader file = new StreamReader(path)) {
					string line = String.Empty;
					int lineNO = 0;
					while ((line = file.ReadLine()) != null) {
						// RemoveEmptyEntries matters here specifically for the calibration lines
						// below: a genuinely blank line (e.g. no stick recalibration has ever
						// been saved yet) must parse to zero entries. line.Split() with no
						// options returns a single-element [""] for an empty string, which would
						// otherwise silently add one bogus entry keyed "" with all-zero data to
						// stickCaliData/stick2CaliData on every startup.
						string[] vs = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
						try {
							if (lineNO < settingsNum) { // load in basic settings
								variables[vs[0]] = vs[1];
							} else if (lineNO == settingsNum) { // load in gyro/accel calibration presets
								caliData.Clear();
								for (int i = 0; i < vs.Length; i++) {
									string[] caliArr = vs[i].Split(',');
									float[] newArr = new float[6];
									for (int j = 1; j < caliArr.Length; j++) {
										newArr[j - 1] = float.Parse(caliArr[j]);
									}
									caliData.Add(new KeyValuePair<string, float[]>(
										caliArr[0],
										newArr
									));
								}
							} else { // load in stick calibration presets (primary stick, then secondary)
								var target = lineNO == settingsNum + 1 ? stickCaliData : stick2CaliData;
								target.Clear();
								for (int i = 0; i < vs.Length; i++) {
									string[] caliArr = vs[i].Split(',');
									ushort[] newArr = new ushort[6];
									for (int j = 1; j < caliArr.Length; j++) {
										newArr[j - 1] = ushort.Parse(caliArr[j]);
									}
									target.Add(new KeyValuePair<string, ushort[]>(
										caliArr[0],
										newArr
									));
								}
							}
						} catch { }
						lineNO++;
					}
				}
			} else {
				using (StreamWriter file = new StreamWriter(path)) {
					foreach (string k in variables.Keys)
						file.WriteLine(String.Format("{0} {1}", k, variables[k]));
					string caliStr = "";
					for (int i = 0; i < caliData.Count; i++) {
						string space = " ";
						if (i == 0) space = "";
						caliStr += space + caliData[i].Key + "," + String.Join(",", caliData[i].Value);
					}
					file.WriteLine(caliStr);
					file.WriteLine(""); // stick calibration (primary) - empty until first recalibration
					file.WriteLine(""); // stick calibration (secondary, Pro controllers only)
				}
			}
		}

		// Live cross-process reload (see HeadlessJoyconHost's FileSystemWatcher) - deliberately
		// far more conservative than Init(): the shared file can be observed mid-write by
		// another process at any time, and this runs on every debounced change, not once at a
		// controlled startup. Never deletes or rewrites the file - a short/malformed read here
		// could just be a transient snapshot of someone else's in-progress write, not evidence
		// the format is genuinely stale, and destroying the user's valid settings over that
		// would be far worse than just retrying on the next watcher event. Parses into a
		// temporary dictionary and only publishes it if the WHOLE read succeeds, so a torn read
		// never partially overwrites a valid in-memory snapshot with a mix of old and new
		// values. Never touches calibration data at all - that's handled entirely in-process by
		// StartCalibration and never needs a file-driven reload.
		public static void ReloadSettingsOnly() {
			if (!File.Exists(path))
				return;

			if (CountLinesInFile(path) < settingsNum)
				return; // possibly just a transient mid-write snapshot - retry next watcher event

			var newVariables = new Dictionary<string, string>();
			foreach (string s in DefaultKeys)
				newVariables[s] = GetDefaultValue(s);

			try {
				using (StreamReader file = new StreamReader(path)) {
					string line;
					int lineNO = 0;
					while ((line = file.ReadLine()) != null && lineNO < settingsNum) {
						string[] vs = line.Split();
						newVariables[vs[0]] = vs[1];
						lineNO++;
					}
				}
			} catch {
				return; // torn/locked read - keep current in-memory settings, retry next event
			}

			variables = newVariables;
		}

		public static int IntValue(string key) {
			if (!variables.ContainsKey(key)) {
				return 0;
			}
			return Int32.Parse(variables[key]);
		}

		public static string Value(string key) {
			if (!variables.ContainsKey(key)) {
				return "";
			}
			return variables[key];
		}

		public static bool SetValue(string key, string value) {
			if (!variables.ContainsKey(key))
				return false;
			variables[key] = value;
			return true;
		}

		public static void SaveCaliData(List<KeyValuePair<string, float[]>> caliData) {
			string[] txt = File.ReadAllLines(path);
			if (txt.Length < settingsNum + 1) // no custom calibrations yet
				Array.Resize(ref txt, txt.Length + 1);

			string caliStr = "";
			for (int i = 0; i < caliData.Count; i++) {
				string space = " ";
				if (i == 0) space = "";
				caliStr += space + caliData[i].Key + "," + String.Join(",", caliData[i].Value);
			}
            txt[settingsNum] = caliStr;
            File.WriteAllLines(path, txt);
		}

		// Two lines after the gyro/accel one: primary stick, then secondary (Pro controllers
		// only). Resizes to fit both regardless of whether SaveCaliData has ever run in this
		// process/file before - the two calls make no assumption about each other's ordering.
		public static void SaveStickCaliData(List<KeyValuePair<string, ushort[]>> stickCaliData, List<KeyValuePair<string, ushort[]>> stick2CaliData) {
			string[] txt = File.ReadAllLines(path);
			int neededLines = settingsNum + 3;
			if (txt.Length < neededLines) {
				int oldLength = txt.Length;
				Array.Resize(ref txt, neededLines);
				for (int i = oldLength; i < neededLines; i++)
					txt[i] = "";
			}

			string stickStr = "";
			for (int i = 0; i < stickCaliData.Count; i++) {
				string space = i == 0 ? "" : " ";
				stickStr += space + stickCaliData[i].Key + "," + String.Join(",", stickCaliData[i].Value);
			}
			txt[settingsNum + 1] = stickStr;

			string stick2Str = "";
			for (int i = 0; i < stick2CaliData.Count; i++) {
				string space = i == 0 ? "" : " ";
				stick2Str += space + stick2CaliData[i].Key + "," + String.Join(",", stick2CaliData[i].Value);
			}
			txt[settingsNum + 2] = stick2Str;

			File.WriteAllLines(path, txt);
		}

		public static void Save() {
			string[] txt = File.ReadAllLines(path);
			int NO = 0;
			foreach (string k in variables.Keys) {
				txt[NO] = String.Format("{0} {1}", k, variables[k]);
				NO++;
			}
			File.WriteAllLines(path, txt);
		}
	}
}
