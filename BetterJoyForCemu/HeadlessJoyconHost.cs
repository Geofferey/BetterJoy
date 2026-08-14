using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;

namespace BetterJoyForCemu {
    // IJoyconHost implementation for running as a Windows Service (see BetterJoyService) - no
    // desktop, so no controller slots/tray icon/dialogs exist. UI-only members are safe no-ops;
    // logging goes to the Windows Event Log instead of a console TextBox. Keyboard/mouse
    // remap/gyro-mouse are forwarded over a named pipe to a session-launched helper process (see
    // InputHelper/SessionLauncher) since Session 0 itself has no desktop for WindowsInput's
    // hooks/injection to attach to.
    public class HeadlessJoyconHost : IJoyconHost {
        private const string EventSource = "BetterJoy";

        private readonly object pipeLock = new object();
        private NamedPipeServerStream pipe;
        private bool loggedNoHelperConnected;

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

        // Starts listening on a brand new named pipe for the next input helper instance to
        // connect to - BetterJoyService launches that helper (via SessionLauncher) right after
        // calling this, passing back the returned pipe name on its command line. Any previous
        // connection is torn down first: used both for the very first helper launch and for
        // relaunching into a newly active session, where the old helper (still holding the now-
        // closed pipe) notices the drop and exits on its own (see InputHelper.Run).
        public string StartNewHelperSession() {
            lock (pipeLock) {
                ClosePipeLocked();

                string pipeName = InputIpc.PipeNamePrefix + Guid.NewGuid().ToString("N");
                var newPipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0,
                    0,
                    InputIpc.CreateCrossSessionPipeSecurity());

                pipe = newPipe;
                newPipe.BeginWaitForConnection(OnHelperConnected, newPipe);
                return pipeName;
            }
        }

        private void OnHelperConnected(IAsyncResult result) {
            var connectedPipe = (NamedPipeServerStream)result.AsyncState;
            try {
                connectedPipe.EndWaitForConnection(result);
            } catch {
                return; // torn down/superseded before the helper connected - fine
            }

            lock (pipeLock) {
                if (connectedPipe != pipe)
                    return; // a newer session already replaced this one
            }

            AppendTextBox("Input helper connected.");
            loggedNoHelperConnected = false;

            var reader = new BinaryReader(connectedPipe);
            Task.Run(() => ReadLoop(connectedPipe, reader));
        }

        private void ReadLoop(NamedPipeServerStream connectedPipe, BinaryReader reader) {
            try {
                while (connectedPipe.IsConnected) {
                    InputMessage msg = InputMessage.ReadFrom(reader);
                    switch (msg.Type) {
                        case InputMessageType.KeyDown: Program.OnKeyDown(msg.A); break;
                        case InputMessageType.KeyUp: Program.OnKeyUp(msg.A); break;
                        case InputMessageType.MouseButtonDown: Program.OnMouseButtonDown(msg.A); break;
                        case InputMessageType.MouseButtonUp: Program.OnMouseButtonUp(msg.A); break;
                    }
                }
            } catch {
                // helper disconnected/crashed - BetterJoyService relaunches on the next session
                // change; nothing to do here but stop reading.
            } finally {
                AppendTextBox("Input helper disconnected.");
            }
        }

        private void ClosePipeLocked() {
            if (pipe != null) {
                try { pipe.Dispose(); } catch { }
                pipe = null;
            }
        }

        private void SendMessage(InputMessageType type, int a = 0, int b = 0) {
            lock (pipeLock) {
                if (pipe == null || !pipe.IsConnected) {
                    if (!loggedNoHelperConnected) {
                        loggedNoHelperConnected = true;
                        AppendTextBox("No input helper connected yet - keyboard/mouse remap and gyro-mouse aren't available until a user is logged on.");
                    }
                    return;
                }

                try {
                    var writer = new BinaryWriter(pipe);
                    new InputMessage { Type = type, A = a, B = b }.WriteTo(writer);
                    writer.Flush();
                } catch {
                    // best-effort - a mid-write disconnect just drops this one command
                }
            }
        }

        public void SimulateKeyClick(int keyCode) => SendMessage(InputMessageType.SimulateKeyClick, keyCode);
        public void SimulateKeyHold(int keyCode) => SendMessage(InputMessageType.SimulateKeyHold, keyCode);
        public void SimulateKeyRelease(int keyCode) => SendMessage(InputMessageType.SimulateKeyRelease, keyCode);
        public void SimulateButtonClick(int buttonCode) => SendMessage(InputMessageType.SimulateButtonClick, buttonCode);
        public void SimulateButtonHold(int buttonCode) => SendMessage(InputMessageType.SimulateButtonHold, buttonCode);
        public void SimulateButtonRelease(int buttonCode) => SendMessage(InputMessageType.SimulateButtonRelease, buttonCode);
        public void SimulateMoveTo(int x, int y) => SendMessage(InputMessageType.SimulateMoveTo, x, y);
        public void SimulateMoveBy(int dx, int dy) => SendMessage(InputMessageType.SimulateMoveBy, dx, dy);
        public void SimulateMoveToScreenCenter() => SendMessage(InputMessageType.SimulateMoveToScreenCenter);
    }
}
