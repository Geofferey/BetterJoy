using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Xml.Linq;

namespace BetterJoyForCemu {
    // One logical controller shown in Controller Profiles. A joined L+R pair is one profile;
    // the same two Joy-Cons when split are two different profiles. ConnectionSequence is the
    // newest physical member's creation order and lets the dialog select the most recently
    // connected logical controller without relying on PadId (which is only a transient slot).
    public sealed class ControllerProfileInfo {
        public string ProfileId { get; set; }
        public string DisplayName { get; set; }
        public long ConnectionSequence { get; set; }
        public bool IsConnected { get; set; }

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
            // active_gyro is retained only to migrate existing per-profile bindings from the
            // former global GyroToJoyOrMouse selector. New runtime/UI code uses the three
            // independent activation keys below.
            "reset_mouse", "active_gyro", "active_gyro_mouse",
            "active_gyro_left_stick", "active_gyro_right_stick",
            "left_click", "right_click",
            "center_click", "scroll_up", "scroll_down", "clench_gyro",
        };

        // Profile-owned behavior which historically lived in App.config. App.config remains the
        // migration/default source for profiles without an explicit option, but these values are
        // persisted beside bindings once a profile is edited.
        public static readonly string[] OptionKeys = {
            "UseAs", "AutoPowerOff", "PowerOffInactivity", "HomeLongPowerOff",
            "GyroHoldToggle", "DragToggle", "SwapAB", "SwapXY", "HomeLEDOn",
        };

        public const string UseAsXbox360 = "xbox360";
        public const string UseAsDualShock4 = "dualshock4";
        public const string UseAsNone = "none";

        private static readonly HashSet<string> AppConfigBackedKeys = new HashSet<string>(StringComparer.Ordinal) {
            "left_click", "right_click", "center_click", "scroll_up", "scroll_down",
            "clench_gyro",
        };

        private static readonly HashSet<string> KnownKeys = new HashSet<string>(Keys, StringComparer.Ordinal);
        private static readonly HashSet<string> KnownOptionKeys =
            new HashSet<string>(OptionKeys, StringComparer.Ordinal);
        private static readonly HashSet<string> GyroActivationKeys = new HashSet<string>(StringComparer.Ordinal) {
            "active_gyro_mouse", "active_gyro_left_stick", "active_gyro_right_stick",
        };
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
                snapshot.TryGetValue(profileId, out profile)) {
                if (profile.TryGetValue(key, out value))
                    return value;

                // Profiles saved before independent gyro outputs only contain active_gyro.
                // Preserve that controller's custom bind for whichever output the old global
                // selector targeted, instead of falling all the way back to another controller's
                // global/default bind.
                if (GyroActivationKeys.Contains(key) &&
                    profile.TryGetValue("active_gyro", out value))
                    return MigrateGyroActivationValue(key, value);
            }

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
                    next[profileId] = profile;
                }

                SnapshotMissingProfileValues(profile);
                profile[key] = String.IsNullOrEmpty(value) ? "0" : value;
                profiles = next;
            }
        }

        public static string OptionValue(string profileId, string key) {
            if (!KnownOptionKeys.Contains(key))
                throw new ArgumentException("Unknown controller profile option: " + key, nameof(key));

            EnsureLoaded();
            Dictionary<string, Dictionary<string, string>> snapshot = profiles;
            Dictionary<string, string> profile;
            string value;
            if (!String.IsNullOrEmpty(profileId) &&
                snapshot.TryGetValue(profileId, out profile) &&
                profile.TryGetValue(key, out value))
                return value;
            return LegacyOptionValue(key);
        }

        public static bool BoolOption(string profileId, string key) {
            bool value;
            return Boolean.TryParse(OptionValue(profileId, key), out value) && value;
        }

        public static int IntOption(string profileId, string key, int fallback = -1) {
            int value;
            return Int32.TryParse(OptionValue(profileId, key), out value) ? value : fallback;
        }

        public static void SetOptionValue(string profileId, string key, string value) {
            if (String.IsNullOrEmpty(profileId))
                throw new ArgumentException("A controller profile is required.", nameof(profileId));
            if (!KnownOptionKeys.Contains(key))
                throw new ArgumentException("Unknown controller profile option: " + key, nameof(key));

            EnsureLoaded();
            lock (writeLock) {
                var next = CloneProfiles(profiles);
                Dictionary<string, string> profile;
                if (!next.TryGetValue(profileId, out profile)) {
                    profile = new Dictionary<string, string>(StringComparer.Ordinal);
                    next[profileId] = profile;
                }

                SnapshotMissingProfileValues(profile);
                profile[key] = value ?? LegacyOptionValue(key);
                profiles = next;
            }
        }

        public static bool AnyVirtualOutputEnabled() {
            EnsureLoaded();
            if (LegacyOptionValue("UseAs") != UseAsNone)
                return true;
            return profiles.Values.Any(profile => {
                string value;
                return profile.TryGetValue("UseAs", out value) && value != UseAsNone;
            });
        }

        public static string DefaultValue(string key) {
            if (GyroActivationKeys.Contains(key))
                return LegacyGyroActivationValue(key);
            return AppConfigBackedKeys.Contains(key) ? "0" : Config.GetDefaultValue(key);
        }

        public static void Save() {
            EnsureLoaded();
            lock (writeLock) {
                var root = new XElement("controllerMappings", new XAttribute("version", "2"));
                foreach (KeyValuePair<string, Dictionary<string, string>> profile in profiles.OrderBy(p => p.Key, StringComparer.Ordinal)) {
                    var profileElement = new XElement("profile", new XAttribute("id", profile.Key));
                    foreach (string key in Keys) {
                        string value;
                        if (profile.Value.TryGetValue(key, out value))
                            profileElement.Add(new XElement("bind", new XAttribute("key", key), new XAttribute("value", value ?? "0")));
                    }
                    foreach (string key in OptionKeys) {
                        string value;
                        if (profile.Value.TryGetValue(key, out value))
                            profileElement.Add(new XElement("option", new XAttribute("key", key), new XAttribute("value", value ?? String.Empty)));
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
                    foreach (XElement optionElement in profileElement.Elements("option")) {
                        string key = (string)optionElement.Attribute("key");
                        string value = (string)optionElement.Attribute("value");
                        if (KnownOptionKeys.Contains(key) && value != null)
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
                    IsConnected = true,
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
                IsConnected = true,
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

        // Profiles are durable configuration, not merely a view of attached hardware. Merge the
        // saved IDs from controller_mappings.xml with the live controller list so the editor can
        // reopen and modify a controller's bindings while that controller is powered off. A live
        // entry replaces its saved-only rendering and retains connection ordering; disconnected
        // entries are sorted by their stable, derived display name after all connected entries.
        public static List<ControllerProfileInfo> IncludeDisconnectedProfiles(
            IEnumerable<ControllerProfileInfo> connectedProfiles) {
            EnsureLoaded();

            var merged = new Dictionary<string, ControllerProfileInfo>(StringComparer.Ordinal);
            Dictionary<string, Dictionary<string, string>> snapshot = profiles;
            foreach (string profileId in snapshot.Keys) {
                merged[profileId] = new ControllerProfileInfo {
                    ProfileId = profileId,
                    DisplayName = DisconnectedDisplayName(profileId),
                    ConnectionSequence = -1,
                    IsConnected = false,
                };
            }

            if (connectedProfiles != null) {
                foreach (ControllerProfileInfo connected in connectedProfiles) {
                    if (connected != null && !String.IsNullOrEmpty(connected.ProfileId)) {
                        connected.IsConnected = true;
                        merged[connected.ProfileId] = connected;
                    }
                }
            }

            return merged.Values
                .OrderByDescending(p => p.IsConnected)
                .ThenByDescending(p => p.ConnectionSequence)
                .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
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
            if (GyroActivationKeys.Contains(key))
                return LegacyGyroActivationValue(key);

            string value = AppConfigBackedKeys.Contains(key)
                ? ConfigurationManager.AppSettings[key]
                : Config.Value(key);
            return String.IsNullOrEmpty(value) ? "0" : value;
        }

        private static string LegacyOptionValue(string key) {
            if (key == "UseAs") {
                bool showAsXbox;
                bool showAsDs4;
                Boolean.TryParse(ConfigurationManager.AppSettings["ShowAsXInput"], out showAsXbox);
                Boolean.TryParse(ConfigurationManager.AppSettings["ShowAsDS4"], out showAsDs4);
                return showAsXbox
                    ? UseAsXbox360
                    : (showAsDs4 ? UseAsDualShock4 : UseAsNone);
            }

            string value = ConfigurationManager.AppSettings[key];
            return value ?? String.Empty;
        }

        private static void SnapshotMissingProfileValues(Dictionary<string, string> profile) {
            foreach (string key in Keys) {
                if (!profile.ContainsKey(key))
                    profile[key] = LegacyValue(key);
            }
            foreach (string key in OptionKeys) {
                if (!profile.ContainsKey(key))
                    profile[key] = LegacyOptionValue(key);
            }
        }

        private static string LegacyGyroActivationValue(string key) {
            return MigrateGyroActivationValue(key, Config.Value("active_gyro"));
        }

        private static string MigrateGyroActivationValue(string key, string legacyBind) {
            string legacyMode = ConfigurationManager.AppSettings["GyroToJoyOrMouse"] ?? "none";
            string matchingMode = key == "active_gyro_mouse"
                ? "mouse"
                : (key == "active_gyro_left_stick" ? "joy_left" : "joy_right");
            if (!String.Equals(legacyMode, matchingMode, StringComparison.Ordinal))
                return "0"; // disabled

            // The old active_gyro convention used 0 for always enabled. The new independent
            // mappings need 0 to mean disabled, otherwise removing the selector would turn all
            // three outputs on together. Preserve the old behavior explicitly for its one target.
            return String.IsNullOrEmpty(legacyBind) || legacyBind == "0"
                ? "always"
                : legacyBind;
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

        private static string DisconnectedDisplayName(string profileId) {
            int separator = profileId == null ? -1 : profileId.IndexOf(':');
            if (separator <= 0 || separator == profileId.Length - 1)
                return (profileId ?? "Controller profile") + " (disconnected)";

            string kind = profileId.Substring(0, separator);
            string identity = profileId.Substring(separator + 1);
            string name;
            switch (kind) {
                case "pair":
                    string[] pair = identity.Split('+');
                    name = pair.Length == 2
                        ? "Joy-Con Pair (L " + IdentitySuffix(pair[0]) + " / R " + IdentitySuffix(pair[1]) + ")"
                        : "Joy-Con Pair (" + IdentitySuffix(identity) + ")";
                    break;
                case "pro":
                    name = "Pro Controller (" + IdentitySuffix(identity) + ")";
                    break;
                case "snes":
                    name = "SNES Controller (" + IdentitySuffix(identity) + ")";
                    break;
                case "n64":
                    name = "N64 Controller (" + IdentitySuffix(identity) + ")";
                    break;
                case "solo-left":
                    name = "Left Joy-Con (solo) (" + IdentitySuffix(identity) + ")";
                    break;
                case "solo-right":
                    name = "Right Joy-Con (solo) (" + IdentitySuffix(identity) + ")";
                    break;
                case "vertical-left":
                    name = "Left Joy-Con (vertical) (" + IdentitySuffix(identity) + ")";
                    break;
                case "vertical-right":
                    name = "Right Joy-Con (vertical) (" + IdentitySuffix(identity) + ")";
                    break;
                default:
                    name = "Controller profile (" + IdentitySuffix(identity) + ")";
                    break;
            }

            return name + " (disconnected)";
        }

        private static string IdentitySuffix(string identity) {
            if (String.IsNullOrEmpty(identity))
                return "unknown";

            string value = identity;
            if (identity.StartsWith("serial-", StringComparison.Ordinal)) {
                string encoded = identity.Substring("serial-".Length)
                    .Replace('-', '+').Replace('_', '/');
                while (encoded.Length % 4 != 0)
                    encoded += "=";
                try {
                    value = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                } catch {
                    value = identity;
                }
            }

            string suffix = value.Substring(Math.Max(0, value.Length - 6));
            return suffix.ToUpperInvariant();
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
