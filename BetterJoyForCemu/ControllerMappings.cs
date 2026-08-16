using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Xml.Linq;

namespace BetterJoyForCemu {
    // One logical controller shown in Map Special Buttons. A joined L+R pair is one profile;
    // the same two Joy-Cons when split are two different profiles. ConnectionSequence is the
    // newest physical member's creation order and lets the dialog select the most recently
    // connected logical controller without relying on PadId (which is only a transient slot).
    public sealed class ControllerProfileInfo {
        public string ProfileId { get; set; }
        public string DisplayName { get; set; }
        public long ConnectionSequence { get; set; }

        public override string ToString() {
            return DisplayName;
        }
    }

    // Per-logical-controller special-button mappings. The old settings/App.config values remain
    // the fallback for controllers which do not have a profile yet, preserving every existing
    // user's mappings. The first edit to a profile snapshots all fallback values so controllers
    // become fully independent rather than retaining a hidden dependency on later global edits.
    public static class ControllerMappings {
        public const string FileName = "controller_mappings.xml";

        public static readonly string[] Keys = {
            "capture", "home", "sl_l", "sl_r", "sr_l", "sr_r", "shake",
            "reset_mouse", "active_gyro", "left_click", "right_click",
            "center_click", "scroll_up", "scroll_down", "clench_gyro",
        };

        private static readonly HashSet<string> AppConfigBackedKeys = new HashSet<string>(StringComparer.Ordinal) {
            "left_click", "right_click", "center_click", "scroll_up", "scroll_down",
            "clench_gyro",
        };

        private static readonly HashSet<string> KnownKeys = new HashSet<string>(Keys, StringComparer.Ordinal);
        private static readonly object writeLock = new object();
        private static volatile Dictionary<string, Dictionary<string, string>> profiles =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        private static bool loaded;

        private static string PathOnDisk => Path.Combine(AppPaths.DataDir, FileName);

        public static string Value(string profileId, string key) {
            EnsureLoaded();

            Dictionary<string, Dictionary<string, string>> snapshot = profiles;
            Dictionary<string, string> profile;
            string value;
            if (!String.IsNullOrEmpty(profileId) &&
                snapshot.TryGetValue(profileId, out profile) &&
                profile.TryGetValue(key, out value))
                return value;

            return LegacyValue(key);
        }

        public static void SetValue(string profileId, string key, string value) {
            if (String.IsNullOrEmpty(profileId))
                throw new ArgumentException("A connected controller profile is required.", nameof(profileId));
            if (!KnownKeys.Contains(key))
                throw new ArgumentException("Unknown controller mapping key: " + key, nameof(key));

            EnsureLoaded();
            lock (writeLock) {
                var next = CloneProfiles(profiles);
                Dictionary<string, string> profile;
                if (!next.TryGetValue(profileId, out profile)) {
                    profile = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (string knownKey in Keys)
                        profile[knownKey] = LegacyValue(knownKey);
                    next[profileId] = profile;
                }

                profile[key] = String.IsNullOrEmpty(value) ? "0" : value;
                profiles = next;
            }
        }

        public static string DefaultValue(string key) {
            return AppConfigBackedKeys.Contains(key) ? "0" : Config.GetDefaultValue(key);
        }

        public static void Save() {
            EnsureLoaded();
            lock (writeLock) {
                var root = new XElement("controllerMappings", new XAttribute("version", "1"));
                foreach (KeyValuePair<string, Dictionary<string, string>> profile in profiles.OrderBy(p => p.Key, StringComparer.Ordinal)) {
                    var profileElement = new XElement("profile", new XAttribute("id", profile.Key));
                    foreach (string key in Keys) {
                        string value;
                        if (profile.Value.TryGetValue(key, out value))
                            profileElement.Add(new XElement("bind", new XAttribute("key", key), new XAttribute("value", value ?? "0")));
                    }
                    root.Add(profileElement);
                }

                string path = PathOnDisk;
                string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
                try {
                    new XDocument(root).Save(temporaryPath);
                    if (File.Exists(path))
                        File.Replace(temporaryPath, path, null, true);
                    else
                        File.Move(temporaryPath, path);
                } finally {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
            }
        }

        // Parse and publish as one immutable snapshot. A service watcher can observe a save at
        // any point; malformed/locked reads keep the last known-good mappings intact.
        public static void Reload() {
            string path = PathOnDisk;
            if (!File.Exists(path)) {
                lock (writeLock) {
                    profiles = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                    loaded = true;
                }
                return;
            }

            Dictionary<string, Dictionary<string, string>> parsed;
            try {
                XDocument document = XDocument.Load(path);
                XElement root = document.Root;
                if (root == null || root.Name != "controllerMappings")
                    return;

                parsed = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                foreach (XElement profileElement in root.Elements("profile")) {
                    string profileId = (string)profileElement.Attribute("id");
                    if (String.IsNullOrEmpty(profileId) || parsed.ContainsKey(profileId))
                        continue;

                    var values = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (XElement bindElement in profileElement.Elements("bind")) {
                        string key = (string)bindElement.Attribute("key");
                        string value = (string)bindElement.Attribute("value");
                        if (KnownKeys.Contains(key) && value != null)
                            values[key] = value;
                    }
                    parsed[profileId] = values;
                }
            } catch {
                return;
            }

            lock (writeLock) {
                profiles = parsed;
                loaded = true;
            }
        }

        public static string ProfileIdFor(Joycon joycon) {
            if (joycon == null)
                return null;

            string ownId = DeviceId(joycon);
            if (joycon.isSnes)
                return "snes:" + ownId;
            if (joycon.is64)
                return "n64:" + ownId;
            if (joycon.isPro)
                return "pro:" + ownId;
            if (joycon.other == joycon)
                return (joycon.isLeft ? "vertical-left:" : "vertical-right:") + ownId;
            if (joycon.other == null)
                return (joycon.isLeft ? "solo-left:" : "solo-right:") + ownId;

            Joycon left = joycon.isLeft ? joycon : joycon.other;
            Joycon right = joycon.isLeft ? joycon.other : joycon;
            return "pair:" + DeviceId(left) + "+" + DeviceId(right);
        }

        public static ControllerProfileInfo ProfileFor(Joycon joycon) {
            if (joycon == null)
                return null;

            if (joycon.other != null && joycon.other != joycon) {
                Joycon left = joycon.isLeft ? joycon : joycon.other;
                Joycon right = joycon.isLeft ? joycon.other : joycon;
                return new ControllerProfileInfo {
                    ProfileId = ProfileIdFor(joycon),
                    DisplayName = "Joy-Con Pair (L " + DeviceSuffix(left) + " / R " + DeviceSuffix(right) + ")",
                    ConnectionSequence = Math.Max(left.virtualControllerSequence, right.virtualControllerSequence),
                };
            }

            string type;
            if (joycon.isSnes)
                type = "SNES Controller";
            else if (joycon.is64)
                type = "N64 Controller";
            else if (joycon.isPro)
                type = "Pro Controller";
            else if (joycon.other == joycon)
                type = joycon.isLeft ? "Left Joy-Con (vertical)" : "Right Joy-Con (vertical)";
            else
                type = joycon.isLeft ? "Left Joy-Con (solo)" : "Right Joy-Con (solo)";

            return new ControllerProfileInfo {
                ProfileId = ProfileIdFor(joycon),
                DisplayName = type + " (" + DeviceSuffix(joycon) + ")",
                ConnectionSequence = joycon.virtualControllerSequence,
            };
        }

        public static List<ControllerProfileInfo> ConnectedProfiles(IEnumerable<Joycon> joycons) {
            var result = new Dictionary<string, ControllerProfileInfo>(StringComparer.Ordinal);
            if (joycons == null)
                return new List<ControllerProfileInfo>();

            foreach (Joycon joycon in joycons) {
                if (joycon == null || (joycon.other != null && joycon.other != joycon && !joycon.isLeft))
                    continue;

                ControllerProfileInfo info = ProfileFor(joycon);
                if (info != null)
                    result[info.ProfileId] = info;
            }

            return result.Values.OrderByDescending(p => p.ConnectionSequence).ToList();
        }

        private static void EnsureLoaded() {
            if (loaded)
                return;
            lock (writeLock) {
                if (loaded)
                    return;
                Reload();
                loaded = true;
            }
        }

        private static string LegacyValue(string key) {
            string value = AppConfigBackedKeys.Contains(key)
                ? ConfigurationManager.AppSettings[key]
                : Config.Value(key);
            return String.IsNullOrEmpty(value) ? "0" : value;
        }

        private static Dictionary<string, Dictionary<string, string>> CloneProfiles(
            Dictionary<string, Dictionary<string, string>> source) {
            var clone = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Dictionary<string, string>> profile in source)
                clone[profile.Key] = new Dictionary<string, string>(profile.Value, StringComparer.Ordinal);
            return clone;
        }

        private static string DeviceId(Joycon joycon) {
            string mac = AddressString(joycon.PadMacAddress);
            if (IsUsableMac(mac))
                return mac.ToLowerInvariant();

            string serial = (joycon.serial_number ?? String.Empty).Trim();
            if (IsUsableMac(serial.ToUpperInvariant()))
                return serial.ToLowerInvariant();
            if (serial.Length > 0 && serial != "000000000001")
                return "serial-" + EncodeIdentity(serial);

            // Some third-party/USB devices expose no unique serial. The HID path is the best
            // available identity in that case; it is deliberately namespaced so it can never
            // collide with a real MAC or serial-backed controller.
            return "path-" + EncodeIdentity(joycon.path ?? String.Empty);
        }

        private static string DeviceSuffix(Joycon joycon) {
            string mac = AddressString(joycon.PadMacAddress);
            if (IsUsableMac(mac))
                return mac.Substring(Math.Max(0, mac.Length - 6)).ToUpperInvariant();

            string serial = (joycon.serial_number ?? String.Empty).Trim();
            if (serial.Length > 0 && serial != "000000000001")
                return serial.Substring(Math.Max(0, serial.Length - 6));
            return "Pad " + (joycon.PadId + 1);
        }

        private static string AddressString(PhysicalAddress address) {
            return address == null ? String.Empty : address.ToString();
        }

        private static bool IsUsableMac(string mac) {
            return mac.Length == 12 && mac != "000000000000" && mac != "010203040506" &&
                mac.All(Uri.IsHexDigit);
        }

        private static string EncodeIdentity(string value) {
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
            return encoded.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}
