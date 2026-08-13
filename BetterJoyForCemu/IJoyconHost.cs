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
    }
}
