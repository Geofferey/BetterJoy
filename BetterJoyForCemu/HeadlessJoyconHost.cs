using System;
using System.Collections.Concurrent;
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
    // logging goes to the Windows Event Log instead of a console TextBox. After login, desktop
    // input is forwarded to a session helper for hooks and ordinary Windows fallback. Before
    // login (or whenever that helper disconnects), controller mouse output goes straight from
    // LocalSystem through FakerInput's virtual HID device; it needs no interactive desktop.
    // Also runs the service side of the GUI control pipe (see ServiceControlProtocol) so a GUI
    // that has deferred hardware ownership here can still show live controller status and
    // trigger rumble test/join-split/calibration.
    public class HeadlessJoyconHost : IJoyconHost {
        private const string EventSource = "BetterJoy";

        private readonly object pipeLock = new object();
        private NamedPipeServerStream pipe;
        private readonly DesktopInputBackend serviceInput = new DesktopInputBackend(false);
        private readonly HashSet<int> heldDesktopMouseButtons = new HashSet<int>();
        private bool helperReady;
        private bool helperOutputSent;
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
        public void RefreshControllerState() { BroadcastSnapshot(); }

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
                            // and recreate later from each solo profile.
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
                v.other.other = null;
                v.other = null;
            }

            Program.mgr.ApplyControllerProfileOptions();
            BroadcastSnapshot();
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

                // Stop the service-side virtual mouse before allowing the helper writer path to
                // observe helperReady=true. WriteMessage uses this same lock, making ownership a
                // strict handoff rather than a race where both processes can emit one report.
                serviceInput.ReleaseVirtualMouseState(false);
                helperReady = true;
                helperOutputSent = false;
                ReapplyHeldButtonsToHelperLocked(connectedPipe);
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
                lock (pipeLock) {
                    if (connectedPipe == pipe) {
                        TransferOutputToServiceLocked();
                    } else if (!helperReady) {
                        // A deliberately superseded helper has now completed its own neutral
                        // shutdown report. It is finally safe to restore any controller button
                        // still physically held if its replacement did not connect in time.
                        serviceInput.ReleaseVirtualMouseState(true);
                        foreach (int buttonCode in heldDesktopMouseButtons)
                            serviceInput.ButtonHold(buttonCode);
                    }
                }
                AppendTextBox("Input helper disconnected.");
            }
        }

        private void ClosePipeLocked() {
            // This is the deliberate replacement path (logon/unlock/console switch), not an
            // unexpected helper loss. Neutralize the old owner but do not immediately restore a
            // held button through the service: the old helper still has to observe the pipe close
            // and its own final neutral report could otherwise release the freshly restored hold.
            if (helperReady || helperOutputSent) {
                helperReady = false;
                if (helperOutputSent)
                    serviceInput.ReleaseVirtualMouseState(true);
                helperOutputSent = false;
            }
            if (pipe != null) {
                try { pipe.Dispose(); } catch { }
                pipe = null;
            }
        }

        // SendMessage is called inline from a Joycon's own poll thread - for gyro-mouse, once per
        // packet, at controller report rate. A synchronous pipe write there would stall that
        // thread for however long the write+flush takes (named pipe buffer contention, the helper
        // process being briefly busy, scheduling noise, ...), which delays draining the next HID
        // report right along with it - one source of the lag/jitter reported in service mode vs
        // the GUI (which calls WindowsInput in-process, no pipe involved). SendMessage now only
        // enqueues; a dedicated writer thread (started once, for the host's lifetime) does the
        // actual I/O off that critical path. Bounded so a genuinely stuck/disconnected pipe can't
        // grow this without limit - if the writer thread has fallen far enough behind to fill it,
        // dropping the newest message is preferable to blocking the caller waiting for room.
        private readonly BlockingCollection<InputMessage> outgoingMessages =
            new BlockingCollection<InputMessage>(new ConcurrentQueue<InputMessage>(), 64);

        // Relative and exact-cursor MoveBy deltas bypass outgoingMessages and accumulate here
        // instead - a dropped discrete event (a click, a key) is a one-off glitch, but a dropped
        // move is displacement that's just gone, permanently, and gyro-mouse calls this at
        // controller report rate. Coalescing means a writer thread that falls behind under
        // sustained motion never loses movement, just delivers it in a bigger jump next flush
        // instead of a bounded queue silently dropping the newest deltas - which is what a
        // fixed-capacity queue was doing here before, and reads as exactly the "constrained, then
        // releases" cycling reported (queue fills during a burst, drains during a lull).
        private readonly object pendingMoveLock = new object();
        private int pendingMoveDx, pendingMoveDy;
        private bool hasPendingMove;
        private int pendingCursorMoveDx, pendingCursorMoveDy;
        private bool hasPendingCursorMove;
        private int pendingWrappedCursorMoveDx, pendingWrappedCursorMoveDy;
        private bool hasPendingWrappedCursorMove;

        public HeadlessJoyconHost() {
            new Thread(PipeWriterLoop) { IsBackground = true, Name = "InputPipeWriter" }.Start();
        }

        // Bounded-wait instead of GetConsumingEnumerable's indefinite block, so this thread wakes
        // up on its own to flush a pending mouse move even when no discrete event is queued -
        // gyro-mouse alone, with nothing else bound, would otherwise never produce one.
        private void PipeWriterLoop() {
            while (true) {
                if (outgoingMessages.TryTake(out InputMessage msg, 5))
                    WriteMessage(msg);

                FlushPendingMove();
            }
        }

        private void FlushPendingMove() {
            int dx, dy, cursorDx, cursorDy, wrappedCursorDx, wrappedCursorDy;
            bool sendRelative, sendCursor, sendWrappedCursor;
            lock (pendingMoveLock) {
                if (!hasPendingMove && !hasPendingCursorMove && !hasPendingWrappedCursorMove)
                    return;
                dx = pendingMoveDx;
                dy = pendingMoveDy;
                cursorDx = pendingCursorMoveDx;
                cursorDy = pendingCursorMoveDy;
                wrappedCursorDx = pendingWrappedCursorMoveDx;
                wrappedCursorDy = pendingWrappedCursorMoveDy;
                sendRelative = hasPendingMove;
                sendCursor = hasPendingCursorMove;
                sendWrappedCursor = hasPendingWrappedCursorMove;
                pendingMoveDx = 0;
                pendingMoveDy = 0;
                pendingCursorMoveDx = 0;
                pendingCursorMoveDy = 0;
                pendingWrappedCursorMoveDx = 0;
                pendingWrappedCursorMoveDy = 0;
                hasPendingMove = false;
                hasPendingCursorMove = false;
                hasPendingWrappedCursorMove = false;
            }

            if (sendRelative)
                WriteMessage(new InputMessage { Type = InputMessageType.SimulateMoveBy, A = dx, B = dy });
            if (sendCursor)
                WriteMessage(new InputMessage { Type = InputMessageType.SimulateCursorMoveBy, A = cursorDx, B = cursorDy });
            if (sendWrappedCursor)
                WriteMessage(new InputMessage { Type = InputMessageType.SimulateWrappedCursorMoveBy, A = wrappedCursorDx, B = wrappedCursorDy });
        }

        private void WriteMessage(InputMessage msg) {
            lock (pipeLock) {
                UpdateHeldButtonStateLocked(msg);

                if (!helperReady || pipe == null || !pipe.IsConnected) {
                    if (!loggedNoHelperConnected) {
                        loggedNoHelperConnected = true;
                        AppendTextBox("No input helper connected - routing controller mouse output through FakerInput for the login/lock screen when available. Keyboard and physical input-hook mappings wait for login.");
                    }
                    ExecuteServiceInputLocked(msg);
                    return;
                }

                try {
                    var writer = new BinaryWriter(pipe);
                    msg.WriteTo(writer);
                    writer.Flush();
                    helperOutputSent = true;
                } catch {
                    // Transfer the current report too: a mid-write disconnect is exactly when
                    // pre-login/lock-screen fallback is needed, and mouse displacement must not
                    // disappear merely because output ownership changed.
                    TransferOutputToServiceLocked();

                    // A hold was already folded into heldDesktopMouseButtons by
                    // UpdateHeldButtonStateLocked above, so the transfer loop just re-applied it -
                    // this only reaches here with helperReady true (the early-return above takes
                    // any !helperReady case), so that loop always runs and always includes it.
                    // Executing it again here would hold it a second time.
                    if (msg.Type != InputMessageType.SimulateButtonHold)
                        ExecuteServiceInputLocked(msg);
                }
            }
        }

        private void UpdateHeldButtonStateLocked(InputMessage msg) {
            if (msg.Type == InputMessageType.SimulateButtonHold)
                heldDesktopMouseButtons.Add(msg.A);
            else if (msg.Type == InputMessageType.SimulateButtonRelease)
                heldDesktopMouseButtons.Remove(msg.A);
        }

        private void ExecuteServiceInputLocked(InputMessage msg) {
            DesktopInputBackend.Execute(msg, serviceInput);
        }

        private void TransferOutputToServiceLocked() {
            if (!helperReady && !helperOutputSent)
                return;

            helperReady = false;
            if (helperOutputSent)
                serviceInput.ReleaseVirtualMouseState(true);
            helperOutputSent = false;

            foreach (int buttonCode in heldDesktopMouseButtons)
                serviceInput.ButtonHold(buttonCode);
        }

        private void ReapplyHeldButtonsToHelperLocked(NamedPipeServerStream connectedPipe) {
            if (heldDesktopMouseButtons.Count == 0)
                return;

            try {
                var writer = new BinaryWriter(connectedPipe);
                foreach (int buttonCode in heldDesktopMouseButtons)
                    new InputMessage { Type = InputMessageType.SimulateButtonHold, A = buttonCode }.WriteTo(writer);
                writer.Flush();
                helperOutputSent = true;
            } catch {
                TransferOutputToServiceLocked();
            }
        }

        public void StopInputRouting() {
            lock (pipeLock) {
                helperReady = false;
                helperOutputSent = false;
                serviceInput.ReleaseVirtualMouseState(false);
                serviceInput.Dispose();
                if (pipe != null) {
                    try { pipe.Dispose(); } catch { }
                    pipe = null;
                }
            }
        }

        private void SendMessage(InputMessageType type, int a = 0, int b = 0) {
            outgoingMessages.TryAdd(new InputMessage { Type = type, A = a, B = b });
        }

        // Hold/Release represent persistent state on the desktop (a key or mouse button actually
        // staying down) rather than a one-off action - silently dropping a Release under queue
        // pressure the way SendMessage's TryAdd does leaves whatever it was supposed to release
        // stuck down indefinitely, which is a correctness bug, not just a missed input. Blocks
        // (via BlockingCollection.Add) instead of dropping. Safe from deadlock even with no
        // helper ever connected: the writer thread keeps draining outgoingMessages regardless of
        // pipe state (WriteMessage no-ops but still consumes the item), so the queue always frees
        // up - this only actually blocks the caller (a Joycon's poll thread) for the rare moment
        // the queue is genuinely full, not routinely like the old synchronous pipe write did.
        private void SendStatefulMessage(InputMessageType type, int a = 0, int b = 0) {
            outgoingMessages.Add(new InputMessage { Type = type, A = a, B = b });
        }

        public void SimulateKeyClick(int keyCode) => SendMessage(InputMessageType.SimulateKeyClick, keyCode);
        public void SimulateKeyHold(int keyCode) => SendStatefulMessage(InputMessageType.SimulateKeyHold, keyCode);
        public void SimulateKeyRelease(int keyCode) => SendStatefulMessage(InputMessageType.SimulateKeyRelease, keyCode);
        public void SimulateButtonClick(int buttonCode) => SendMessage(InputMessageType.SimulateButtonClick, buttonCode);
        public void SimulateButtonHold(int buttonCode) => SendStatefulMessage(InputMessageType.SimulateButtonHold, buttonCode);
        public void SimulateButtonRelease(int buttonCode) => SendStatefulMessage(InputMessageType.SimulateButtonRelease, buttonCode);
        public void SimulateMoveTo(int x, int y) => SendMessage(InputMessageType.SimulateMoveTo, x, y);

        public void SimulateMoveBy(int dx, int dy) {
            lock (pendingMoveLock) {
                pendingMoveDx += dx;
                pendingMoveDy += dy;
                hasPendingMove = true;
            }
        }

        public void SimulateCursorMoveBy(int dx, int dy) {
            lock (pendingMoveLock) {
                pendingCursorMoveDx += dx;
                pendingCursorMoveDy += dy;
                hasPendingCursorMove = true;
            }
        }

        public void SimulateWrappedCursorMoveBy(int dx, int dy) {
            lock (pendingMoveLock) {
                pendingWrappedCursorMoveDx += dx;
                pendingWrappedCursorMoveDy += dy;
                hasPendingWrappedCursorMove = true;
            }
        }

        public void SimulateMoveToScreenCenter() => SendMessage(InputMessageType.SimulateMoveToScreenCenter);
        public void SimulateScroll(bool up) => SendMessage(InputMessageType.SimulateScroll, up ? 1 : 0);

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
                        case ControlMessageType.CalibrationReady:
                            reader.ReadByte(); // padId - only one calibration can be in progress at a time, guarded by calibrationInProgress
                            CompleteCalibReady();
                            break;
                        case ControlMessageType.StartButtonCapture:
                            StartButtonCapture();
                            break;
                        case ControlMessageType.StopButtonCapture:
                            StopButtonCapture();
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

        // Mirrors MainForm's calibration step sequence exactly - a connected GUI renders the
        // same CalibrationDialog off of the CalibrationStep messages pushed below.
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

        // Completed by an incoming CalibrationReady message (see ControlReadLoop) whenever the
        // GUI's Start or Done button is clicked - both use the same message/wait, since only one
        // can ever be pending at a time and the flow below knows from context which it means.
        // Gyro is the one phase that doesn't wait on this a second time (no Done click - see
        // CalibrationDialog for why): it runs on its own fixed Task.Delay instead once started.
        private TaskCompletionSource<bool> pendingCalibReady;

        private async void StartCalibration(int padId) {
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

            // Target/Secondary mirror MainForm's CalibStep - a Pro controller's two sticks live
            // on ONE Joycon object (distinguished by Secondary), but a joined pair's two sticks
            // live on TWO SEPARATE Joycon objects, each needing its own calibration pass against
            // that specific instance. Without this, only whichever half jc happens to be would
            // ever actually get recalibrated - the partner's stick would silently stay untouched.
            var stickSteps = new List<(Joycon Target, bool Secondary, string Label)>();
            bool isPair = jc.other != null && jc.other != jc;
            if (jc.isPro) {
                stickSteps.Add((jc, false, "Left Stick"));
                stickSteps.Add((jc, true, "Right Stick"));
            } else if (isPair) {
                Joycon leftJc = jc.isLeft ? jc : jc.other;
                Joycon rightJc = jc.isLeft ? jc.other : jc;
                stickSteps.Add((leftJc, false, "Left Stick"));
                stickSteps.Add((rightJc, false, "Right Stick"));
            } else {
                stickSteps.Add((jc, false, jc.isLeft ? "Left Stick" : "Right Stick"));
            }
            int totalSteps = 1 + stickSteps.Count;

            try {
                SendCalibrationStep(padId, 1, totalSteps, "Gyroscope", "Place the controller on a flat, still surface.", CalibStepUiMode.Start, 0);
                CalibrationState.PendingConfirmController = jc;
                await WaitForCalibReady();

                CalibrationState.ClearSamples();
                CalibrationState.CalibratingController = jc;
                CalibrationState.Calibrating = true;
                for (int t = 3; t >= 0; t--) {
                    SendCalibrationStep(padId, 1, totalSteps, "Gyroscope", "Hold still...", CalibStepUiMode.Countdown, t);
                    if (t > 0)
                        await Task.Delay(1000);
                }

                CalibrationState.FinishCalibration(jc.serial_number);
                jc.getActiveData();

                for (int i = 0; i < stickSteps.Count; i++) {
                    Joycon target = stickSteps[i].Target;
                    bool secondary = stickSteps[i].Secondary;
                    string stepName = stickSteps[i].Label;
                    int stepNumber = i + 2; // step 1 is always Gyro

                    CalibrationState.ClearStickSamples();
                    CalibrationState.StickCalibratingController = target;
                    CalibrationState.StickCalibrating = true;
                    CalibrationState.CurrentStickTarget = secondary ? CalibrationState.StickTarget.Secondary : CalibrationState.StickTarget.Primary;
                    CalibrationState.CurrentStickPhase = CalibrationState.StickPhase.None;

                    // Center gets a Start gate (the user needs a moment to actually let go of/
                    // center the stick before admission begins), then just a brief automatic
                    // capture - centering is a quick snapshot, not something that benefits from
                    // open-ended time. Rotate below skips the Start gate too: a few stray samples
                    // before the user starts moving are harmless, they just fall inside the
                    // eventual min/max instead of corrupting it.
                    SendCalibrationStep(padId, stepNumber, totalSteps, stepName, String.Format("Leave the {0} centered - don't touch it.", stepName.ToLower()), CalibStepUiMode.Start, 0);
                    CalibrationState.PendingConfirmController = target;
                    await WaitForCalibReady();
                    CalibrationState.CurrentStickPhase = CalibrationState.StickPhase.Center;
                    await Task.Delay(1000);
                    CalibrationState.CurrentStickPhase = CalibrationState.StickPhase.None;

                    CalibrationState.CurrentStickPhase = CalibrationState.StickPhase.Range;
                    SendCalibrationStep(padId, stepNumber, totalSteps, stepName, String.Format("Now rotate the {0} in full circles out to its edges.", stepName.ToLower()), CalibStepUiMode.Done, 0);
                    CalibrationState.PendingConfirmController = target;
                    await WaitForCalibReady();
                    CalibrationState.CurrentStickPhase = CalibrationState.StickPhase.None;

                    CalibrationState.FinishStickCalibration(target.serial_number, secondary);
                    target.getActiveStickData();
                }

                SendControlMessage(w => ServiceControlIpc.WritePadIdMessage(w, ControlMessageType.CalibrationComplete, padId));
            } catch {
                // Any step can fail (e.g. the controller disconnected mid-window) -
                // calibrationInProgress and the CalibrationState flags MUST still clear here or
                // every future calibration request fails until the service restarts.
                CalibrationState.Calibrating = false;
                CalibrationState.CalibratingController = null;
                CalibrationState.StickCalibrating = false;
                CalibrationState.StickCalibratingController = null;
                CalibrationState.PendingConfirmController = null;
                SendControlMessage(w => ServiceControlIpc.WritePadIdMessage(w, ControlMessageType.CalibrationFailed, padId));
            } finally {
                pendingCalibReady = null;
                Interlocked.Exchange(ref calibrationInProgress, 0);
            }
        }

        private Task WaitForCalibReady() {
            // RunContinuationsAsynchronously - TrySetResult below fires from either the pipe's
            // read loop thread (ControlReadLoop) or a physical controller's own Poll thread (see
            // HandleCalibrationConfirm); without this the rest of StartCalibration's async
            // continuation would run inline on whichever one completed it, delaying that thread
            // from getting back to its own work.
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingCalibReady = tcs;
            return tcs.Task;
        }

        // Shared completion path for both an incoming CalibrationReady pipe message (a remote
        // GUI's mouse click) and a physical confirm button press on the controller itself (see
        // HandleCalibrationConfirm below) - whichever fires first is the one that counts.
        private void CompleteCalibReady() {
            CalibrationState.PendingConfirmController = null;
            pendingCalibReady?.TrySetResult(true);
        }

        // IJoyconHost - called from the calibrating controller's own Poll thread (see Joycon.
        // DoThingsWithButtons) when a face button is pressed while a Start/Done prompt is
        // showing. No live GUI here to update directly (unlike MainForm's implementation) - just
        // completing the same wait a CalibrationReady pipe message would have is enough; the
        // async StartCalibration continuation does the rest, including pushing the next
        // CalibrationStep to whatever remote GUI is connected.
        public void HandleCalibrationConfirm(Joycon joycon) {
            CompleteCalibReady();
        }

        private void SendCalibrationStep(int padId, int stepNumber, int totalSteps, string stepName, string instruction, CalibStepUiMode uiMode, int count) {
            SendControlMessage(w => ServiceControlIpc.WriteCalibrationStep(w, new CalibrationStepInfo {
                PadId = padId,
                StepNumber = stepNumber,
                TotalSteps = totalSteps,
                StepName = stepName,
                Instruction = instruction,
                UiMode = uiMode,
                Count = count,
            }));
        }

        // Server-side counterpart to Reassign.JoyPoll_Tick's local polling - the GUI has no
        // Joycon instances of its own to poll when it's deferred hardware ownership here, so
        // this does the same rising/falling edge detection against Program.mgr.j (which IS
        // populated in this process) and pushes each transition over the control pipe instead of
        // acting on it directly. System.Timers.Timer, not a WinForms Timer - this process has no
        // message pump for a WM_TIMER-based timer to ever fire on.
        private System.Timers.Timer buttonCapturePoll;
        private readonly Dictionary<Joycon, bool[]> buttonCapturePrev = new Dictionary<Joycon, bool[]>();

        private void StartButtonCapture() {
            if (buttonCapturePoll != null)
                return; // already running for this (or another) connected GUI

            buttonCapturePrev.Clear();
            buttonCapturePoll = new System.Timers.Timer(30);
            buttonCapturePoll.Elapsed += (s, e) => PollButtonCapture();
            buttonCapturePoll.AutoReset = true;
            buttonCapturePoll.Start();
        }

        private void StopButtonCapture() {
            buttonCapturePoll?.Stop();
            buttonCapturePoll?.Dispose();
            buttonCapturePoll = null;
            buttonCapturePrev.Clear();
        }

        private void PollButtonCapture() {
            if (Program.mgr == null)
                return;

            int buttonCount = Enum.GetValues(typeof(Joycon.Button)).Length;
            foreach (Joycon jc in Program.mgr.j) {
                // See the identical skip in Reassign.cs's JoyPoll_Tick - a joined pair's two
                // halves cross-reference each other's raw buttons, so polling both independently
                // reports one physical press as two (e.g. "DPAD_DOWN+B" for a single B press).
                // The left half alone already has a complete, correctly-labeled view of the pair.
                if (jc.other != null && jc.other != jc && !jc.isLeft)
                    continue;

                string profileId = ControllerMappings.ProfileIdFor(jc);

                if (!buttonCapturePrev.TryGetValue(jc, out bool[] prev)) {
                    prev = new bool[buttonCount];
                    for (int bi = 0; bi < buttonCount; bi++)
                        prev[bi] = jc.GetButton((Joycon.Button)bi);
                    buttonCapturePrev[jc] = prev;
                    continue; // baseline only - don't report a controller's already-held buttons as fresh presses
                }

                for (int bi = 0; bi < buttonCount; bi++) {
                    bool now = jc.GetButton((Joycon.Button)bi);
                    if (now == prev[bi])
                        continue;
                    prev[bi] = now;
                    int capturedBi = bi;
                    bool capturedNow = now;
                    SendControlMessage(w => ServiceControlIpc.WriteButtonTransition(w, profileId, capturedBi, capturedNow));
                }
            }
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
                ControllerProfileInfo profile = ControllerMappings.ProfileFor(jc);

                records.Add(new ControllerRecord {
                    PadId = (byte)jc.PadId,
                    Kind = kind,
                    Battery = (sbyte)jc.battery,
                    OtherPadId = otherPadId,
                    ProfileId = profile.ProfileId,
                    ProfileName = profile.DisplayName,
                    ConnectionSequence = profile.ConnectionSequence,
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
            "settings", "BetterJoyForCemu.exe.config", ControllerMappings.FileName,
            "3rdPartyControllers", "BlacklistedControllers",
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
                ControllerMappings.Reload();
                ConfigurationManager.RefreshSection("appSettings");

                // Program.thirdPartyCons/blacklistedCons are plain Lists a scan pass iterates
                // directly - rebuilding them (Clear()+AddRange()) outside the scan lock could
                // race that iteration. See JoyconManager.RunExclusiveOfScanning.
                Program.mgr?.RunExclusiveOfScanning(_3rdPartyControllers.LoadIntoProgramLists);
                Program.mgr?.ApplyControllerProfileOptions();

                AppendTextBox("Reloaded shared configuration after a change.");
            } catch (Exception ex) {
                AppendTextBox("Failed to reload shared configuration: " + ex.Message);
            }
        }
    }
}
