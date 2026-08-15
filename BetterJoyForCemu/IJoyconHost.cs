namespace BetterJoyForCemu {
    // Abstracts everything Program.cs/Joycon.cs/UdpServer.cs need from a "host" - a real MainForm
    // in GUI mode, or a no-UI headless host when running as a Windows Service (Session 0 has no
    // desktop, so there's nothing for a tray icon/controller slots to exist on). Implementations
    // own whatever thread-marshaling their own state needs internally (e.g. MainForm.Invoke), so
    // callers never need to know whether they're talking to a real Control or not.
    public interface IJoyconHost {
        void AppendTextBox(string message);

        // UI-only - real work in GUI mode; safe no-ops headless, since there's no controller
        // slot/tray icon to update without a desktop.
        void AssignSlot(Joycon joycon);
        void CollapseJoinedPair(Joycon left, Joycon right);
        void HandleJoyconDropped(Joycon dropped, Joycon survivingPartner);
        void JoinOrSplitJoycon(Joycon joycon);
        void NotifyLowBattery(Joycon joycon);
        void UpdateBatteryColor(Joycon joycon);

        // Called from the controller's own Poll thread (see Joycon.DoThingsWithButtons) when a
        // face button is pressed while CalibrationState.PendingConfirmController names this exact
        // controller - lets the user confirm a calibration Start/Done prompt from the controller
        // itself instead of reaching for the mouse. A no-op whenever nothing is actually pending.
        void HandleCalibrationConfirm(Joycon joycon);

        // Keyboard/mouse injection for remap (SL/SR/Capture -> key/mouse bind) and gyro-mouse.
        // Codes are WindowsInput.Events.KeyCode/ButtonCode cast to int, kept untyped here so this
        // interface doesn't need a WindowsInput dependency. Needs an interactive desktop to
        // actually do anything - GUI mode calls WindowsInput.Simulate directly (same as always);
        // headless/service mode forwards the request over a pipe to a session-launched helper
        // process that can (see HeadlessJoyconHost/SessionLauncher), since Session 0 has none.
        void SimulateKeyClick(int keyCode);
        void SimulateKeyHold(int keyCode);
        void SimulateKeyRelease(int keyCode);
        void SimulateButtonClick(int buttonCode);
        void SimulateButtonHold(int buttonCode);
        void SimulateButtonRelease(int buttonCode);
        void SimulateMoveTo(int x, int y);
        void SimulateMoveBy(int dx, int dy);
        void SimulateCursorMoveBy(int dx, int dy);

        // Deliberately not "SimulateMoveTo(screenWidth/2, screenHeight/2)" computed by the
        // caller - Screen.PrimaryScreen is only meaningful wherever the actual desktop is, which
        // for a Windows Service is the session-launched helper, not the service process itself.
        // Each implementation resolves "center" in its own context.
        void SimulateMoveToScreenCenter();

        // One scroll wheel tick - up (true) or down (false). Left/right/middle click reuse the
        // existing SimulateButtonHold/Release above (ButtonCode.Left/Right/Middle); scroll has no
        // hold/release equivalent, just a discrete tick per press.
        void SimulateScroll(bool up);
    }
}
