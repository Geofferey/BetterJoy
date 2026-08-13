using System;
using System.Diagnostics;

namespace BetterJoyForCemu {
    // IJoyconHost implementation for running as a Windows Service (see BetterJoyService) - no
    // desktop, so no controller slots/tray icon/dialogs exist. UI-only members are safe no-ops;
    // logging goes to the Windows Event Log instead of a console TextBox.
    public class HeadlessJoyconHost : IJoyconHost {
        private const string EventSource = "BetterJoy";

        public void AppendTextBox(string message) {
            try {
                if (!EventLog.SourceExists(EventSource))
                    EventLog.CreateEventSource(EventSource, "Application");
                EventLog.WriteEntry(EventSource, message, EventLogEntryType.Information);
            } catch {
                // Logging must never be the reason the service goes down.
            }
        }

        public void AssignSlot(Joycon joycon) { }
        public void CollapseJoinedPair(Joycon left, Joycon right) { }
        public void HandleJoyconDropped(Joycon dropped, Joycon survivingPartner) { }
        public void UpdateBatteryColor(Joycon joycon) { }

        public void NotifyLowBattery(Joycon joycon) {
            AppendTextBox(String.Format("Controller {0} - low battery.", joycon.PadId));
        }

        // Hardware stick-double-click join/split is a controller-slot UI feature (see
        // MainForm.JoinOrSplitJoycon) not reimplemented for headless mode yet - log it once
        // rather than silently doing nothing forever.
        private bool loggedJoinSplitUnsupported = false;
        public void JoinOrSplitJoycon(Joycon joycon) {
            if (!loggedJoinSplitUnsupported) {
                loggedJoinSplitUnsupported = true;
                AppendTextBox("Joining/splitting Joycons via hardware stick double-click isn't supported when running as a service.");
            }
        }
    }
}
