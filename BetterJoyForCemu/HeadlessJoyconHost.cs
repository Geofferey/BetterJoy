using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BetterJoyForCemu {
    // IJoyconHost implementation for running as a Windows Service (see BetterJoyService) - no
    // desktop, so no controller slots/tray icon/dialogs exist. UI-only members are safe no-ops;
    // logging goes to the Windows Event Log instead of a console TextBox. Keyboard/mouse
    // remap/gyro-mouse are forwarded over a named pipe to a session-launched helper process (see
    // InputHelper/SessionLauncher) since Session 0 itself has no desktop for WindowsInput's
    // hooks/injection to attach to. Also runs the service side of the GUI control pipe (see
    // ServiceControlProtocol) so a GUI that has deferred hardware ownership here can still show
    // live controller status and trigger rumble test/join-split/calibration.
    public class HeadlessJoyconHost : IJoyconHost {
        private const string EventSource = "BetterJoy";

        private readonly object pipeLock = new object();
        private NamedPipeServerStream pipe;
        private bool loggedNoHelperConnected;

        private readonly object controlPipeLock = new object();
        private NamedPipeServerStream controlPipe;

        public void AppendTextBox(string message) {
            try {
                if (!EventLog.SourceExists(EventSource))
                    EventLog.CreateEventSource(EventSource, "Application");
                EventLog.WriteEntry(EventSource, message, EventLogEntryType.Information);
            } catch {
                // Logging must never be the reason the service goes down.
            }
        }

        public void AssignSlot(Joycon joycon) { BroadcastSnapshot(); }
        public void CollapseJoinedPair(Joycon left, Joycon right) { BroadcastSnapshot(); }
        public void HandleJoyconDropped(Joycon dropped, Joycon survivingPartner) { BroadcastSnapshot(); }
        public void UpdateBatteryColor(Joycon joycon) { BroadcastSnapshot(); }

        public void NotifyLowBattery(Joycon joycon) {
            AppendTextBox(String.Format("Controller {0} - low battery.", joycon.PadId));
        }

        // Mirrors MainForm.JoinOrSplitJoycon (MainForm.cs) minus the button/icon updates, which
        // don't exist headless - used both for the hardware stick-double-click path (Joycon.cs
        // calls this via IJoyconHost the same as GUI mode) and the remote JoinOrSplit command
        // from a connected GUI (see JoinOrSplitByPadId below).
        public void JoinOrSplitJoycon(Joycon v) {
            if (v.other == null && !v.isPro) {
                if (Program.mgr.j.Count == 1) {
                    v.other = v; // self-pair - single joycon in vertical mode
                } else {
                    foreach (Joycon jc in Program.mgr.j) {
                        if (!jc.isPro && jc.isLeft != v.isLeft && jc != v && jc.other == null) {
                            v.other = jc;
                            jc.other = v;

                            // Disconnect whichever controller was created later - see Joycon.
                            // virtualControllerSequence. The older one is the one most likely
                            // already locked onto by a running game, so it's left untouched; the
                            // newer one is safe to actually disconnect (matching a real unplug)
                            // and recreate later on split via ReenableViGEm.
                            Joycon loser = v.virtualControllerSequence > jc.virtualControllerSequence ? v : jc;
                            if (loser.out_xbox != null) {
                                try { loser.out_xbox.Disconnect(); } catch { }
                                loser.out_xbox = null;
                            }
                            if (loser.out_ds4 != null) {
                                try { loser.out_ds4.Disconnect(); } catch { }
                                loser.out_ds4 = null;
                            }
                            break;
                        }
                    }
                }
            } else if (v.other != null && !v.isPro) {
                // Recreates whichever controller was actually disconnected on join (see above) -
                // a no-op for whichever one was never touched, since ReenableViGEm only acts
                // when out_xbox/out_ds4 is null.
                ReenableViGEm(v);
                ReenableViGEm(v.other);

                v.other.other = null;
                v.other = null;
            }

            BroadcastSnapshot();
        }

        private void ReenableViGEm(Joycon v) {
            bool showAsXInput = Boolean.Parse(ConfigurationManager.AppSettings["ShowAsXInput"]);
            bool showAsDS4 = Boolean.Parse(ConfigurationManager.AppSettings["ShowAsDS4"]);
            bool toRumble = Boolean.Parse(ConfigurationManager.AppSettings["EnableRumble"]);

            if (showAsXInput && v.out_xbox == null) {
                v.out_xbox = new Controller.OutputControllerXbox360();
                if (toRumble)
                    v.out_xbox.FeedbackReceived += v.ReceiveRumble;
                v.out_xbox.Connect();
            }

            if (showAsDS4 && v.out_ds4 == null) {
                v.out_ds4 = new Controller.OutputControllerDualShock4();
                if (toRumble)
                    v.out_ds4.FeedbackReceived += v.Ds4_FeedbackReceived;
                v.out_ds4.Connect();
            }
        }

        // ---------------------------------------------------------------------------------
        // Input helper pipe (keyboard/mouse remap) - see InputHelper/SessionLauncher.
        // ---------------------------------------------------------------------------------

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

        // ---------------------------------------------------------------------------------
        // GUI control pipe (live status + rumble test/join-split/calibration commands) - see
        // ServiceControlProtocol. Unlike the input helper pipe above, this one is long-lived
        // and reconnectable: a GUI may start/stop independently of the service's own lifetime,
        // so this keeps accepting new connections for as long as the service runs.
        // ---------------------------------------------------------------------------------

        public void StartControlServer() {
            AcceptNextControlConnection();
        }

        // The real fix for the DACL missing a SYSTEM grant lives in
        // InputIpc.CreateCrossSessionPipeSecurity (see its comment) - creating a brand new
        // object succeeds regardless of its own DACL, but every instance after the first of an
        // already-existing named pipe is checked against it, so without an explicit grant for
        // the service's own account, only the first accept-loop iteration ever worked. Reusing
        // one PipeSecurity instance here isn't load-bearing for that bug, but avoids rebuilding
        // it on every reconnect for no reason.
        private static readonly PipeSecurity ControlPipeSecurity = InputIpc.CreateCrossSessionPipeSecurity();

        private void AcceptNextControlConnection() {
            // This whole control channel is a status/convenience feature layered on top of the
            // core HID/ViGEm pipeline, which has nothing to do with it - an exception here
            // (pipe creation failing for any reason) must never be allowed to propagate and take
            // the whole service down with it, the way the DACL bug above did. Log and stop
            // trying rather than crash; a GUI just won't get live status until the service is
            // restarted with whatever caused this fixed.
            try {
                var newPipe = new NamedPipeServerStream(
                    ServiceControlIpc.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0,
                    0,
                    ControlPipeSecurity);

                newPipe.BeginWaitForConnection(OnControlClientConnected, newPipe);
            } catch (Exception ex) {
                AppendTextBox("GUI control pipe stopped accepting connections: " + ex.Message);
            }
        }

        private void OnControlClientConnected(IAsyncResult result) {
            var connectedPipe = (NamedPipeServerStream)result.AsyncState;
            try {
                connectedPipe.EndWaitForConnection(result);
            } catch {
                return; // service stopping - the pipe was disposed out from under the wait
            }

            lock (controlPipeLock) {
                if (controlPipe != null) {
                    try { controlPipe.Dispose(); } catch { }
                }
                controlPipe = connectedPipe;
            }

            AppendTextBox("GUI control connection established.");
            BroadcastSnapshot();

            var reader = new BinaryReader(connectedPipe);
            Task.Run(() => ControlReadLoop(connectedPipe, reader));

            // Keep listening immediately, independent of this connection's lifetime, so a GUI
            // relaunch (or a second one, however unlikely given its own single-instance mutex)
            // always finds the pipe accepting.
            AcceptNextControlConnection();
        }

        private void ControlReadLoop(NamedPipeServerStream connectedPipe, BinaryReader reader) {
            try {
                while (connectedPipe.IsConnected) {
                    var type = (ControlMessageType)reader.ReadByte();
                    switch (type) {
                        case ControlMessageType.RequestSnapshot:
                            BroadcastSnapshot();
                            break;
                        case ControlMessageType.TestRumble:
                            TestRumble(reader.ReadByte());
                            break;
                        case ControlMessageType.JoinOrSplit:
                            JoinOrSplitByPadId(reader.ReadByte());
                            break;
                        case ControlMessageType.StartCalibration:
                            StartCalibration(reader.ReadByte());
                            break;
                    }
                }
            } catch {
                // GUI closed/disconnected - fine, a fresh accept-loop is already listening.
            } finally {
                AppendTextBox("GUI control connection closed.");
            }
        }

        private void TestRumble(int padId) {
            Joycon jc = Program.mgr?.j.FirstOrDefault(j => j.PadId == padId);
            if (jc == null)
                return;

            jc.SetRumble(160.0f, 320.0f, 1.0f);
            Task.Delay(300).ContinueWith(_ => jc.SetRumble(160.0f, 320.0f, 0));
        }

        private void JoinOrSplitByPadId(int padId) {
            Joycon jc = Program.mgr?.j.FirstOrDefault(j => j.PadId == padId);
            if (jc != null)
                JoinOrSplitJoycon(jc);
        }

        // Mirrors MainForm's StartGetData/CalcData timing (calibration samples are gathered
        // into the shared CalibrationState buffers for a fixed ~3s window) - the "keep it flat"
        // pre-countdown is purely a GUI-local visual affordance, so it isn't reproduced here.
        // CalibrationState.CalibratingController scopes sample admission to this one padId, so
        // other connected controllers staying connected/polling can't contaminate the buffers -
        // no need to require exactly one controller connected total.
        //
        // int, not bool: admission is a check-and-set (see StartCalibration), which needs
        // Interlocked.CompareExchange to be atomic - a plain volatile bool read-then-write could
        // let two StartCalibration calls on different threads both pass the check before either
        // sets it, if a stale ControlReadLoop connection hasn't yet realized it's been
        // superseded (see AcceptNextControlConnection) and a new one calls in around the same time.
        private int calibrationInProgress = 0;

        private void StartCalibration(int padId) {
            // Captured once, before the guard - re-querying for it a second time after admission
            // (the controller this validated a moment earlier could disconnect in between) could
            // throw with the guard already set and no continuation left to ever release it.
            // FirstOrDefault never throws either way.
            Joycon jc = Program.mgr?.j.FirstOrDefault(v => v.PadId == padId);
            if (jc == null) {
                SendControlMessage(w => ServiceControlIpc.WritePadIdMessage(w, ControlMessageType.CalibrationFailed, padId));
                return;
            }

            if (Interlocked.CompareExchange(ref calibrationInProgress, 1, 0) != 0) {
                // Already calibrating something - a second request arriving mid-window would
                // call ClearSamples() over an in-progress collection and leave two pending
                // completions racing the same CalibrationState buffers.
                SendControlMessage(w => ServiceControlIpc.WritePadIdMessage(w, ControlMessageType.CalibrationFailed, padId));
                return;
            }

            SendControlMessage(w => ServiceControlIpc.WritePadIdMessage(w, ControlMessageType.CalibrationStarted, padId));

            CalibrationState.ClearSamples();
            CalibrationState.CalibratingController = jc;
            CalibrationState.Calibrating = true;

            Task.Delay(3000).ContinueWith(_ => {
                // The guard and CalibrationState.Calibrating MUST clear even if the controller
                // disconnected mid-window, no samples came in, or the save failed - otherwise
                // calibrationInProgress stays stuck at 1 forever and every future calibration
                // request fails until the service restarts.
                try {
                    CalibrationState.FinishCalibration(jc.serial_number);
                    jc.getActiveData();
                    SendControlMessage(w => ServiceControlIpc.WritePadIdMessage(w, ControlMessageType.CalibrationComplete, padId));
                } catch {
                    SendControlMessage(w => ServiceControlIpc.WritePadIdMessage(w, ControlMessageType.CalibrationFailed, padId));
                } finally {
                    CalibrationState.Calibrating = false;
                    CalibrationState.CalibratingController = null;
                    Interlocked.Exchange(ref calibrationInProgress, 0);
                }
            });
        }

        private void BroadcastSnapshot() {
            SendControlMessage(w => ServiceControlIpc.WriteSnapshot(w, BuildSnapshot()));
        }

        private List<ControllerRecord> BuildSnapshot() {
            var records = new List<ControllerRecord>();
            if (Program.mgr == null)
                return records;

            foreach (Joycon jc in Program.mgr.j) {
                ControllerKind kind = jc.isPro ? ControllerKind.Pro
                    : jc.isSnes ? ControllerKind.Snes
                    : jc.is64 ? ControllerKind.N64
                    : jc.isLeft ? ControllerKind.Left
                    : ControllerKind.Right;

                sbyte otherPadId = (jc.other != null && jc.other != jc) ? (sbyte)jc.other.PadId : (sbyte)-1;

                records.Add(new ControllerRecord {
                    PadId = (byte)jc.PadId,
                    Kind = kind,
                    Battery = (sbyte)jc.battery,
                    OtherPadId = otherPadId,
                });
            }
            return records;
        }

        private void SendControlMessage(Action<BinaryWriter> write) {
            lock (controlPipeLock) {
                if (controlPipe == null || !controlPipe.IsConnected)
                    return;

                try {
                    var writer = new BinaryWriter(controlPipe);
                    write(writer);
                } catch {
                    // best-effort - a mid-write disconnect just drops this one push
                }
            }
        }

        // ---------------------------------------------------------------------------------
        // Shared config auto-reload - picks up settings/keybind/controller-list changes a GUI
        // (or anything else) writes to the shared AppPaths.DataDir location, without needing
        // to restart the service. See EntryPoint.RedirectConfigToAppData/AppPaths for why these
        // files live where they do.
        // ---------------------------------------------------------------------------------

        private static readonly HashSet<string> WatchedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "settings", "BetterJoyForCemu.exe.config", "3rdPartyControllers", "BlacklistedControllers",
        };

        private FileSystemWatcher configWatcher;
        private System.Timers.Timer reloadDebounceTimer;

        public void StartConfigWatcher() {
            reloadDebounceTimer = new System.Timers.Timer(500) { AutoReset = false };
            reloadDebounceTimer.Elapsed += (sender, e) => ReloadSharedConfig();

            configWatcher = new FileSystemWatcher(AppPaths.DataDir) {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            };
            configWatcher.Changed += OnConfigFileChanged;
            configWatcher.Created += OnConfigFileChanged;
            configWatcher.Renamed += OnConfigFileChanged;
            configWatcher.EnableRaisingEvents = true;
        }

        private void OnConfigFileChanged(object sender, FileSystemEventArgs e) {
            if (!WatchedFileNames.Contains(e.Name))
                return;

            // FileSystemWatcher reliably fires more than once per save (e.g. a write-then-
            // rename pattern some editors/APIs use) - restart a short idle timer instead of
            // reloading on every single event.
            reloadDebounceTimer.Stop();
            reloadDebounceTimer.Start();
        }

        private void ReloadSharedConfig() {
            try {
                // Settings/keybinds only, not calibration data (see Config.ReloadSettingsOnly) -
                // calibration is handled entirely in-process by StartCalibration and never
                // needs a file-driven reload, so there's no reason to risk it here.
                Config.ReloadSettingsOnly();
                ConfigurationManager.RefreshSection("appSettings");

                // Program.thirdPartyCons/blacklistedCons are plain Lists a scan pass iterates
                // directly - rebuilding them (Clear()+AddRange()) outside the scan lock could
                // race that iteration. See JoyconManager.RunExclusiveOfScanning.
                Program.mgr?.RunExclusiveOfScanning(_3rdPartyControllers.LoadIntoProgramLists);

                AppendTextBox("Reloaded shared configuration after a change.");
            } catch (Exception ex) {
                AppendTextBox("Failed to reload shared configuration: " + ex.Message);
            }
        }
    }
}
