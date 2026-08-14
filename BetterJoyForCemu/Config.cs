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
		public static void Init(List<KeyValuePair<string, float[]>> caliData) {
			foreach (string s in DefaultKeys)
				variables[s] = GetDefaultValue(s);

			if (File.Exists(path)) {

				// Reset settings file if old settings
				if (CountLinesInFile(path) < settingsNum) {
					File.Delete(path);
					Init(caliData);
					return;
				}

				using (StreamReader file = new StreamReader(path)) {
					string line = String.Empty;
					int lineNO = 0;
					while ((line = file.ReadLine()) != null) {
						string[] vs = line.Split();
						try {
							if (lineNO < settingsNum) { // load in basic settings
								variables[vs[0]] = vs[1];
							} else { // load in calibration presets
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
