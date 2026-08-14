using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Timers;
using System.Windows.Forms;
using BetterJoyForCemu.Collections;
using Nefarius.Drivers.HidHide;
using Nefarius.Utilities.DeviceManagement.PnP;
using Nefarius.ViGEm.Client;
using static BetterJoyForCemu._3rdPartyControllers;
using static BetterJoyForCemu.HIDapi;

namespace BetterJoyForCemu {
    public class JoyconManager {
        public bool EnableIMU = true;
        public bool EnableLocalize = false;

        private const ushort vendor_id = 0x57e;
        private const ushort product_l = 0x2006;
        private const ushort product_r = 0x2007;
        private const ushort product_pro = 0x2009;
        private const ushort product_snes = 0x2017;
        private const ushort product_n64 = 0x2019;

        public ConcurrentList<Joycon> j { get; private set; } // Array of all connected Joy-Cons
        static JoyconManager instance;

        public IJoyconHost form;

        System.Timers.Timer controllerCheck;

        public static JoyconManager Instance {
            get { return instance; }
        }

        public void Awake() {
            instance = this;
            j = new ConcurrentList<Joycon>();
            HIDapi.hid_init();
        }

        public void Start() {
            controllerCheck = new System.Timers.Timer(2000); // check for new controllers every 2 seconds
            controllerCheck.Elapsed += CheckForNewControllersTime;
            controllerCheck.Start();
        }

        bool ControllerAlreadyAdded(string path) {
            foreach (Joycon v in j)
                if (v.path == path)
                    return true;
            return false;
        }

        void CleanUp() { // removes dropped controllers from list
            List<Joycon> rem = new List<Joycon>();
            foreach (Joycon joycon in j) {
                if (joycon.state == Joycon.state_.DROPPED) {
                    // Capture the pair partner (if any) before Detach/nulling below, so
                    // HandleJoyconDropped can still find whichever slot(s) need fixing up -
                    // the dropped Joycon's own slot, and/or the surviving partner's.
                    Joycon partner = (joycon.other != null && joycon.other != joycon) ? joycon.other : null;

                    form.HandleJoyconDropped(joycon, partner);

                    if (joycon.other != null)
                        joycon.other.other = null; // The other of the other is the joycon itself

                    joycon.Detach(true);
                    rem.Add(joycon);

                    form.AppendTextBox("Removed dropped controller. Can be reconnected.\r\n");
                }
            }

            foreach (Joycon v in rem)
                j.Remove(v);
        }

        void CheckForNewControllersTime(Object source, ElapsedEventArgs e) {
            CleanUp();
            if (Boolean.Parse(ConfigurationManager.AppSettings["PassiveScan"])) {
                CheckForNewControllers();
            }
        }

        // Attempts to hide a device via HidHide, retrying a few times (short delay) within this
        // pass since a freshly-plugged-in device's PnP instance can occasionally not be settled
        // yet. Returns true if hidden (or HidHide isn't in use), false if hiding failed after
        // retries - callers decide what that means for them (e.g. skip attaching this pass, or
        // for an already-blacklisted device, nothing further at all).
        private bool TryHideController(hid_device_info enumerate) {
            if (!Program.useHidHide)
                return true;

            string instanceId = null;
            for (int hideAttempt = 0; hideAttempt < 5 && instanceId == null; hideAttempt++) {
                if (hideAttempt > 0)
                    Thread.Sleep(50);

                try {
                    instanceId = PnPDevice.GetInstanceIdFromInterfaceId(enumerate.path);
                    Program.hidHide.AddBlockedInstanceId(instanceId);
                    Program.hiddenInstanceIds.Add(instanceId);
                } catch {
                    instanceId = null;
                }
            }

            return instanceId != null;
        }

        // Walks up the PnP device tree from a HID interface to find the underlying bus (USB or
        // Bluetooth) it's actually connected through. GetInstanceIdFromInterfaceId only resolves
        // to the HID-level device node itself (always prefixed "HID\...", for either transport),
        // not its parent bus device, so checking that string directly never actually
        // distinguishes USB from Bluetooth - the real answer is a few levels up the tree.
        private static void GetControllerTransport(string hidPath, out bool isUsb, out bool isBluetooth) {
            isUsb = false;
            isBluetooth = false;

            IPnPDevice device = PnPDevice.GetDeviceByInterfaceId(hidPath, DeviceLocationFlags.Normal);
            for (int depth = 0; device != null && depth < 8; depth++) {
                if (device.InstanceId.StartsWith("USB", StringComparison.OrdinalIgnoreCase)) {
                    isUsb = true;
                    return;
                }
                if (device.InstanceId.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase)) {
                    isBluetooth = true;
                    return;
                }
                device = device.Parent;
            }
        }

        private ushort TypeToProdId(byte type) {
            switch (type) {
                case 1:
                    return product_pro;
                case 2:
                    return product_l;
                case 3:
                    return product_r;
            }
            return 0;
        }

        public void CheckForNewControllers() {
            // move all code for initializing devices here and well as the initial code from Start()
            bool isLeft = false;
            IntPtr ptr = HIDapi.hid_enumerate(0x0, 0x0);
            IntPtr top_ptr = ptr;

            hid_device_info enumerate; // Add device to list
            bool foundNew = false;
            while (ptr != IntPtr.Zero) {
                SController thirdParty = null;
                enumerate = (hid_device_info)Marshal.PtrToStructure(ptr, typeof(hid_device_info));

                if (enumerate.serial_number == null) {
                    ptr = enumerate.next; // can't believe it took me this long to figure out why USB connections used up so much CPU.
                                          // it was getting stuck in an inf loop here!
                    continue;
                }

                // Blacklisted devices (set from the Add Controllers dialog - e.g. a 3rd-party
                // controller that identifies differently over USB vs Bluetooth, where only one
                // of those identities should be usable) are skipped unconditionally, ahead of
                // both the manual custom-controller match and auto-add below. Still hide it from
                // other programs, though - the same physical controller may be reachable through
                // this identity from another program (e.g. Steam) while BetterJoy uses a
                // different transport/identity for it, which would otherwise let that other
                // program read duplicate raw input alongside BetterJoy's own virtual output.
                bool isBlacklisted = false;
                foreach (SController v in Program.blacklistedCons) {
                    if (enumerate.vendor_id == v.vendor_id && enumerate.product_id == v.product_id && enumerate.serial_number == v.serial_number) {
                        isBlacklisted = true;
                        break;
                    }
                }
                if (isBlacklisted) {
                    TryHideController(enumerate);
                    ptr = enumerate.next;
                    continue;
                }

                bool validController = (enumerate.product_id == product_l || enumerate.product_id == product_r ||
                                        enumerate.product_id == product_pro || enumerate.product_id == product_snes || enumerate.product_id == product_n64) && enumerate.vendor_id == vendor_id;
                // check list of custom controllers specified
                foreach (SController v in Program.thirdPartyCons) {
                    if (enumerate.vendor_id == v.vendor_id && enumerate.product_id == v.product_id && enumerate.serial_number == v.serial_number) {
                        validController = true;
                        thirdParty = v;
                        break;
                    }
                }

                // auto-detect and register new 3rd-party controllers instead of requiring manual setup
                if (!validController && Boolean.Parse(ConfigurationManager.AppSettings["AutoAddControllers"]) && IsGameController(enumerate)) {
                    bool blockedByTransport = false;
                    if (Boolean.Parse(ConfigurationManager.AppSettings["BlockAutoAddUSB"]) || Boolean.Parse(ConfigurationManager.AppSettings["BlockAutoAddBluetooth"])) {
                        try {
                            GetControllerTransport(enumerate.path, out bool isUsbDevice, out bool isBluetoothDevice);
                            blockedByTransport = (isUsbDevice && Boolean.Parse(ConfigurationManager.AppSettings["BlockAutoAddUSB"])) ||
                                                  (isBluetoothDevice && Boolean.Parse(ConfigurationManager.AppSettings["BlockAutoAddBluetooth"]));
                        } catch {
                            // Can't determine transport - fall through and allow auto-add rather
                            // than silently blocking a device we couldn't actually classify.
                        }
                    }

                    if (!blockedByTransport) {
                        thirdParty = new SController(BuildDeviceName(enumerate), enumerate.vendor_id, enumerate.product_id, GuessType(enumerate), enumerate.serial_number);
                        Program.thirdPartyCons.Add(thirdParty);
                        _3rdPartyControllers.PersistCustomController(thirdParty);
                        validController = true;
                        form.AppendTextBox("Auto-added new controller: " + thirdParty + "\r\n");
                    } else {
                        // Same reasoning as the blacklist case above: BetterJoy won't use this
                        // device, but the same physical controller may still be reachable
                        // through it from another program (e.g. Steam) over the transport we're
                        // not using, so hide it from other programs anyway.
                        TryHideController(enumerate);
                    }
                }

                ushort prod_id = thirdParty == null ? enumerate.product_id : TypeToProdId(thirdParty.type);
                if (prod_id == 0) {
                    ptr = enumerate.next; // controller was not assigned a type, but advance ptr anyway
                    continue;
                }

                if (validController && !ControllerAlreadyAdded(enumerate.path)) {
                    switch (prod_id) {
                        case product_l:
                            isLeft = true;
                            form.AppendTextBox("Left Joy-Con connected.\r\n"); break;
                        case product_r:
                            isLeft = false;
                            form.AppendTextBox("Right Joy-Con connected.\r\n"); break;
                        case product_pro:
                            isLeft = true;
                            form.AppendTextBox("Pro controller connected.\r\n"); break;
                        case product_snes:
                            isLeft = true;
                            form.AppendTextBox("SNES controller connected.\r\n"); break;
                        case product_n64:
                            isLeft = true;
                            form.AppendTextBox("N64 controller connected.\r\n"); break;
                        default:
                            form.AppendTextBox("Non Joy-Con Nintendo input device skipped.\r\n"); break;
                    }

                    // Hide this controller (Joycon, Pro, SNES, or N64 - all share this same
                    // connect path) from other programs (e.g. Steam) via HidHide, before opening/
                    // attaching it ourselves below. If it's still not ready to hide after a few
                    // retries, skip attaching it this pass entirely (rather than falling through
                    // and opening it unhidden) so other programs can't grab the raw device and
                    // end up double-processing input alongside our virtual output. The next
                    // periodic scan (2s later) retries again from there.
                    if (!TryHideController(enumerate)) {
                        form.AppendTextBox("Controller not ready to hide yet, will retry.\r\n");
                        ptr = enumerate.next;
                        continue;
                    }
                    // -------------------- //

                    // hid_open_path returns a null handle (rather than throwing) when it can't
                    // open the device - already exclusively held by another process racing for
                    // the same physical device, momentarily unavailable mid-HidHide-toggle, etc.
                    // Passing that straight into hid_set_nonblocking used to crash the whole
                    // process with an unrecoverable AccessViolationException: native access
                    // violations are a "corrupted state exception" the CLR deliberately lets
                    // bypass ordinary try/catch (since .NET 4.0), so the catch here never
                    // actually protected against this - validate the handle first instead.
                    IntPtr handle = HIDapi.hid_open_path(enumerate.path);
                    if (handle == IntPtr.Zero) {
                        form.AppendTextBox("Unable to open path to device - are you using the correct (64 vs 32-bit) version for your PC?\r\n");
                        break;
                    }
                    HIDapi.hid_set_nonblocking(handle, 1);

                    bool isPro = prod_id == product_pro;
                    bool isSnes = prod_id == product_snes;
                    bool is64 = prod_id == product_n64;
                    j.Add(new Joycon(handle, EnableIMU, EnableLocalize & EnableIMU, 0.05f, isLeft, enumerate.path, enumerate.serial_number, j.Count, isPro, isSnes, is64,thirdParty != null));

                    foundNew = true;
                    j.Last().form = form;
                    form.AssignSlot(j.Last());

                    byte[] mac = new byte[6];
                    try {
                        for (int n = 0; n < 6; n++)
                            mac[n] = byte.Parse(enumerate.serial_number.Substring(n * 2, 2), System.Globalization.NumberStyles.HexNumber);
                    } catch (Exception e) {
                        // could not parse mac address
                    }
                    j[j.Count - 1].PadMacAddress = new PhysicalAddress(mac);
                }

                ptr = enumerate.next;
            }

            if (foundNew && !Boolean.Parse(ConfigurationManager.AppSettings["DoNotRejoinJoycons"])) { // attempt to auto join-up joycons on connection
                Joycon temp = null;
                foreach (Joycon v in j) {
                    // Do not attach two controllers if they are either:
                    // - Not a Joycon
                    // - Already attached to another Joycon (that isn't itself)
                    if (v.isPro || (v.other != null && v.other != v)) {
                        continue;
                    }

                    // Otherwise, iterate through and find the Joycon with the lowest
                    // id that has not been attached already (Does not include self)
                    if (temp == null)
                        temp = v;
                    else if (temp.isLeft != v.isLeft && v.other == null) {
                        temp.other = v;
                        v.other = temp;

                        if (temp.out_xbox != null) {
                            try {
                                temp.out_xbox.Disconnect();
                            } catch (Exception e) {
                                // it wasn't connected in the first place, go figure
                            }
                        }
                        if (temp.out_ds4 != null) {
                            try {
                                temp.out_ds4.Disconnect();
                            } catch (Exception e) {
                                // it wasn't connected in the first place, go figure
                            }
                        }
                        temp.out_xbox = null;
                        temp.out_ds4 = null;

                        Joycon left = temp.isLeft ? temp : v;
                        Joycon right = temp.isLeft ? v : temp;
                        form.CollapseJoinedPair(left, right);

                        temp = null;    // repeat
                    }
                }
            }

            HIDapi.hid_free_enumeration(top_ptr);

            bool on = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None).AppSettings.Settings["HomeLEDOn"].Value.ToLower() == "true";
            foreach (Joycon jc in j) { // Connect device straight away
                if (jc.state == Joycon.state_.NOT_ATTACHED) {
                    if (jc.out_xbox != null)
                        jc.out_xbox.Connect();
                    if (jc.out_ds4 != null)
                        jc.out_ds4.Connect();

                    try {
                        jc.Attach();
                    } catch (Exception e) {
                        jc.state = Joycon.state_.DROPPED;
                        continue;
                    }

                    jc.SetHomeLight(on);

                    jc.Begin();
                    if (Boolean.Parse(ConfigurationManager.AppSettings["AllowCalibration"])) {
                        jc.getActiveData();
                    }
                }
            }
        }

        public void OnApplicationQuit() {
            foreach (Joycon v in j) {
                if (Boolean.Parse(ConfigurationManager.AppSettings["AutoPowerOff"]))
                    v.PowerOff();

                v.Detach();

                // A target that was created but never actually got plugged in (or was already
                // unplugged) throws VigemTargetNotPluggedInException on Disconnect() - same
                // "wasn't connected in the first place" case already guarded against elsewhere
                // (see the auto-join block above), just missed here. Left unhandled, this was
                // surfacing as a failed/hung service stop (DeferredStop propagating the
                // exception back to the SCM) rather than a clean shutdown.
                if (v.out_xbox != null) {
                    try { v.out_xbox.Disconnect(); } catch { }
                }

                if (v.out_ds4 != null) {
                    try { v.out_ds4.Disconnect(); } catch { }
                }
            }

            controllerCheck.Stop();
            HIDapi.hid_exit();
        }
    }

    class Program {
        public static PhysicalAddress btMAC = new PhysicalAddress(new byte[] { 0, 0, 0, 0, 0, 0 });
        public static UdpServer server;

        public static ViGEmClient emClient;

        private static readonly HttpClient client = new HttpClient();

        public static JoyconManager mgr;

        static IJoyconHost form;

        // Lets a non-GUI host (BetterJoyService, running headless with no MainForm at all) wire
        // itself in before calling Start(). GUI mode sets this itself via Main() below.
        public static void SetHost(IJoyconHost host) {
            form = host;
        }

        static public bool useHidHide = Boolean.Parse(ConfigurationManager.AppSettings["UseHidHide"]);
        public static IHidHideControlService hidHide;
        public static readonly List<string> hiddenInstanceIds = new List<string>();

        public static List<SController> thirdPartyCons = new List<SController>();
        public static List<SController> blacklistedCons = new List<SController>();

        private static WindowsInput.Events.Sources.IKeyboardEventSource keyboard;
        private static WindowsInput.Events.Sources.IMouseEventSource mouse;

        public static void Start() {
            // Previously only ever called from MainForm_Load, so a Windows Service (which never
            // constructs a MainForm) silently never loaded remap keybinds (capture/home/sl_*/
            // sr_*/shake/reset_mouse/active_gyro) at all - every Config.Value(...) lookup for
            // them returned "" under service mode. Moved here so both modes get it.
            Config.Init(CalibrationState.CaliData);

            if (useHidHide) {
                try {
                    // HidHide's config lives in a read/write-protected registry key (not file-based,
                    // intentionally, so it can't be casually edited outside the driver API):
                    // https://github.com/nefarius/HidHide/discussions/130
                    hidHide = new HidHideControlService();
                    if (!hidHide.IsInstalled) {
                        form.AppendTextBox("HidHide isn't installed - controllers won't be hidden from other programs.\r\n");
                        useHidHide = false;
                    } else {
                        string exePath = Process.GetCurrentProcess().MainModule.FileName;
                        if (!hidHide.ApplicationPaths.Contains(exePath, StringComparer.OrdinalIgnoreCase))
                            hidHide.AddApplicationPath(exePath);
                        hidHide.IsActive = true;
                    }
                } catch (Exception e) {
                    form.AppendTextBox("Unable to configure HidHide - everything should work fine without it. (" + e.GetType().Name + ": " + e.Message + ")\r\n");
                    useHidHide = false;
                }
            }

            if (Boolean.Parse(ConfigurationManager.AppSettings["ShowAsXInput"]) || Boolean.Parse(ConfigurationManager.AppSettings["ShowAsDS4"])) {
                try {
                    emClient = new ViGEmClient(); // Manages emulated XInput
                } catch (Nefarius.ViGEm.Client.Exceptions.VigemBusNotFoundException) {
                    form.AppendTextBox("Could not start VigemBus. Make sure drivers are installed correctly.\r\n");
                }
            }

            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces()) {
                // Get local BT host MAC
                if (nic.NetworkInterfaceType != NetworkInterfaceType.FastEthernetFx && nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) {
                    if (nic.Name.Split()[0] == "Bluetooth") {
                        btMAC = nic.GetPhysicalAddress();
                    }
                }
            }

            // GUI mode goes through the actual Form (also where the Add Controllers dialog's
            // lists get populated for editing); headless/service mode has no desktop for a Form
            // to exist on, so it loads the persisted lists directly instead - see
            // _3rdPartyControllers.LoadIntoProgramLists.
            if (form is MainForm) {
                // a bit hacky
                _3rdPartyControllers partyForm = new _3rdPartyControllers();
                partyForm.CopyCustomControllers();
                partyForm.CopyBlacklistedControllers();
            } else {
                _3rdPartyControllers.LoadIntoProgramLists();
            }

            mgr = new JoyconManager();
            mgr.form = form;
            mgr.Awake();
            mgr.CheckForNewControllers();
            mgr.Start();

            server = new UdpServer(mgr.j);
            server.form = form;

            server.Start(IPAddress.Parse(ConfigurationManager.AppSettings["IP"]), Int32.Parse(ConfigurationManager.AppSettings["Port"]));

            // Global keyboard/mouse hooks need an interactive desktop - fine in GUI mode, but
            // Session 0 (where a Windows Service runs) has none, so this would throw/do nothing
            // useful there. Service mode instead gets forwarded events over a pipe from a
            // session-launched helper process that can (see HeadlessJoyconHost/SessionLauncher).
            if (form is MainForm) {
                keyboard = WindowsInput.Capture.Global.KeyboardAsync();
                keyboard.KeyEvent += (sender, e) => {
                    if (e.Data.KeyDown != null) OnKeyDown((int)e.Data.KeyDown.Key);
                    if (e.Data.KeyUp != null) OnKeyUp((int)e.Data.KeyUp.Key);
                };
                mouse = WindowsInput.Capture.Global.MouseAsync();
                mouse.MouseEvent += (sender, e) => {
                    if (e.Data.ButtonDown != null) OnMouseButtonDown((int)e.Data.ButtonDown.Button);
                    if (e.Data.ButtonUp != null) OnMouseButtonUp((int)e.Data.ButtonUp.Button);
                };
            }

            form.AppendTextBox("All systems go\r\n");
        }

        // Decision logic for the "reset_mouse"/"active_gyro" keyboard/mouse-button binds - kept
        // independent of *how* the raw key/button event was observed, so both GUI mode's direct
        // WindowsInput.Capture.Global hook and service mode's pipe-forwarded events from the
        // session-launched helper can feed into the exact same code.
        public static void OnKeyDown(int keyCode) {
            string res_val = Config.Value("reset_mouse");
            if (res_val.StartsWith("key_"))
                if (keyCode == Int32.Parse(res_val.Substring(4)))
                    form.SimulateMoveToScreenCenter();

            res_val = Config.Value("active_gyro");
            if (res_val.StartsWith("key_"))
                if (keyCode == Int32.Parse(res_val.Substring(4)))
                    foreach (var i in mgr.j)
                        i.active_gyro = true;
        }

        public static void OnKeyUp(int keyCode) {
            string res_val = Config.Value("active_gyro");
            if (res_val.StartsWith("key_"))
                if (keyCode == Int32.Parse(res_val.Substring(4)))
                    foreach (var i in mgr.j)
                        i.active_gyro = false;
        }

        public static void OnMouseButtonDown(int buttonCode) {
            string res_val = Config.Value("reset_mouse");
            if (res_val.StartsWith("mse_"))
                if (buttonCode == Int32.Parse(res_val.Substring(4)))
                    form.SimulateMoveToScreenCenter();

            res_val = Config.Value("active_gyro");
            if (res_val.StartsWith("mse_"))
                if (buttonCode == Int32.Parse(res_val.Substring(4)))
                    foreach (var i in mgr.j)
                        i.active_gyro = true;
        }

        public static void OnMouseButtonUp(int buttonCode) {
            string res_val = Config.Value("active_gyro");
            if (res_val.StartsWith("mse_"))
                if (buttonCode == Int32.Parse(res_val.Substring(4)))
                    foreach (var i in mgr.j)
                        i.active_gyro = false;
        }

        public static void Stop() {
            if (useHidHide && hidHide != null && Boolean.Parse(ConfigurationManager.AppSettings["UnhideOnExit"])) {
                foreach (string id in hiddenInstanceIds) {
                    try { hidHide.RemoveBlockedInstanceId(id); } catch { }
                }
                hiddenInstanceIds.Clear();
            }

            keyboard?.Dispose(); mouse?.Dispose();
            server.Stop();
            mgr.OnApplicationQuit();
        }

        private static string appGuid = "1bf709e9-c133-41df-933a-c9ff3f664c7b"; // randomly-generated
        public static void Main(string[] args) {
            using (Mutex mutex = new Mutex(false, "Global\\" + appGuid)) {
                if (!mutex.WaitOne(0, false)) {
                    MessageBox.Show("Instance already running.", "BetterJoy");
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                MainForm mainForm = new MainForm();
                form = mainForm;
                Application.Run(mainForm);
            }
        }

        // Called from EntryPoint.Main() before branching into GUI/service/input-helper mode, so
        // every mode gets it - previously lived here and only ran for GUI mode, which meant a
        // Windows Service (launched via EntryPoint straight into ServiceBase.Run, bypassing this
        // Main entirely) never got hidapi.dll's directory added to the DLL search path at all,
        // crashing immediately on the first P/Invoke into it (DllNotFoundException).
        public static void SetupDlls() {
            string archPath = $"{AppDomain.CurrentDomain.BaseDirectory}{(Environment.Is64BitProcess ? "x64" : "x86")}\\";
            string pathVariable = Environment.GetEnvironmentVariable("PATH");
            pathVariable = $"{archPath};{pathVariable}";
            Environment.SetEnvironmentVariable("PATH", pathVariable);
        }

        // Helper funtions to set the hidapi dll location acording to the system instruction set.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetDefaultDllDirectories(int directoryFlags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern void AddDllDirectory(string lpPathName);
    }
}
