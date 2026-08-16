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

        // ViGEmBus's default emulated identities (CreateXbox360Controller()/CreateDS4Controller()
        // are called with no VID/PID override anywhere in this codebase - see
        // Controller.OutputControllerXbox360/OutputControllerDualShock4 - so BetterJoy's own
        // virtual output always shows up under these). Windows exposes that virtual pad through a
        // HID interface too, for DirectInput compatibility, which otherwise passes the same
        // generic "is this a gamepad" usage-page/usage check real controllers do - letting
        // AutoAddControllers mistake BetterJoy's own output for a brand new physical controller,
        // whose raw input then just mirrors whatever BetterJoy already sent it, one poll tick
        // later. Checked ahead of the 3rd-party allowlist/auto-add below, not after, so this never
        // becomes a Joycon object in the first place, however AutoAddControllers is configured.
        private const ushort vigemXbox360VendorId = 0x045E;
        private const ushort vigemXbox360ProductId = 0x028E;
        private const ushort vigemDs4VendorId = 0x054C;
        private const ushort vigemDs4ProductId = 0x05C4;

        public ConcurrentList<Joycon> j { get; private set; } // Array of all connected Joy-Cons
        static JoyconManager instance;

        public IJoyconHost form;

        System.Timers.Timer controllerCheck;

        // Guards a scan pass (CleanUp + CheckForNewControllers) end to end. System.Timers.Timer
        // can fire Elapsed again before a slow pass has finished (HidHide retries, PnP calls),
        // so this also prevents two passes running concurrently against the same controller
        // list/hiddenInstanceIds - not just the shutdown race StopScanning uses it for below.
        readonly object scanLock = new object();

        // Timer.Stop() only blocks FUTURE ticks - an Elapsed callback that already fired and is
        // queued on the ThreadPool, but hasn't reached the scanLock yet, isn't covered by that.
        // Set before the rendezvous below (not inside it) so a callback which acquires scanLock
        // at any point after this is set - whether it was already queued or not - sees it and
        // exits without doing any scan work, instead of racing full teardown.
        volatile bool scanningStopped = false;

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

        // Stops future scan passes AND blocks until any pass already in progress (or already
        // queued on the ThreadPool, about to start) has exited without doing further work.
        // controllerCheck.Stop() alone only blocks brand new ticks from firing - a callback that
        // fired just before Stop() but hadn't yet acquired scanLock would find it uncontested
        // and run anyway, racing full teardown (OnApplicationQuit detaching every controller and
        // calling HIDapi.hid_exit()). Setting scanningStopped first closes that: any callback
        // that acquires scanLock after this point - already queued or not - checks the flag
        // before doing anything and exits immediately.
        public void StopScanning() {
            scanningStopped = true;
            controllerCheck?.Stop();
            lock (scanLock) { }
            controllerCheck?.Dispose();
        }

        bool ControllerAlreadyAdded(string path) {
            foreach (Joycon v in j)
                if (v.path == path)
                    return true;
            return false;
        }

        // Smallest PadId not currently in use by a connected controller - see the call site for
        // why j.Count itself isn't safe to use directly.
        int NextAvailablePadId() {
            var used = new HashSet<int>();
            foreach (Joycon v in j)
                used.Add(v.PadId);

            int id = 0;
            while (used.Contains(id))
                id++;
            return id;
        }

        void CleanUp() { // removes dropped controllers from list
            List<Joycon> rem = new List<Joycon>();
            List<Joycon> droppedNotify = new List<Joycon>();
            List<Joycon> partnerNotify = new List<Joycon>();

            foreach (Joycon joycon in j) {
                if (joycon.state == Joycon.state_.DROPPED) {
                    // Capture the pair partner (if any) before Detach/nulling below, so
                    // HandleJoyconDropped can still find whichever slot(s) need fixing up -
                    // the dropped Joycon's own slot, and/or the surviving partner's.
                    Joycon partner = (joycon.other != null && joycon.other != joycon) ? joycon.other : null;

                    if (joycon.other != null) {
                        Joycon survivor = joycon.other;
                        survivor.other = null; // The other of the other is the joycon itself

                        // The survivor needs its own controller back if the dropped half was the
                        // one actively driving the pair's shared controller (see JoinOrSplitJoycon/
                        // the auto-join block above, which disconnects whichever side loses the
                        // join) - otherwise it's left solo with no virtual controller at all.
                        // No-op if the survivor already has one (it was the active side).
                        CreateOutputControllers(survivor);
                    }

                    joycon.Detach(true);
                    rem.Add(joycon);

                    droppedNotify.Add(joycon);
                    partnerNotify.Add(partner);

                    form.AppendTextBox("Removed dropped controller. Can be reconnected.\r\n");
                }
            }

            foreach (Joycon v in rem)
                j.Remove(v);

            // Compact PadIds for whatever's left so dropping down to fewer controllers -
            // especially down to just one - reads as player 1 again, matching what unplugging
            // and replugging the physical controller already does today (which is exactly what
            // this is standing in for, so the user doesn't have to do that by hand). See
            // ReassignPadIds for why this also means tearing down and recreating each affected
            // survivor's virtual controller, not just its LED.
            if (rem.Count > 0)
                ReassignPadIds();

            // Notified only after removal is fully done (list + pairing), not before - MainForm's
            // implementation doesn't care (it acts on the passed-in Joycon references directly,
            // not by re-reading the controller list), but HeadlessJoyconHost's does: it rebuilds
            // its status snapshot from the live list, and building that snapshot before the
            // drop was actually applied meant a GUI connected to a running service never saw a
            // disconnect at all until it reconnected (the next change - a different controller
            // connecting - would finally push a snapshot that happened to already be missing it).
            for (int i = 0; i < droppedNotify.Count; i++)
                form.HandleJoyconDropped(droppedNotify[i], partnerNotify[i]);
        }

        // Compacts PadId assignments for whatever's currently in j down to 0..n-1, based on each
        // controller's existing PadId order. Dropping controllers can leave survivors holding
        // non-contiguous PadIds (e.g. players 1 and 3 remain after player 2 disconnects) - this
        // closes the gaps, most visibly in the "down to just one controller left" case, which
        // should read as player 1 without the user having to unplug/replug it by hand.
        //
        // Unlike the old LED-only version, a controller whose PadId actually changes gets its
        // virtual controller torn down and recreated at the new identity via AssignPadId, not
        // just a re-painted LED: ViGEmBus has no API to rename an already-Connect()ed target's
        // XInput slot, so a fresh Connect() is how identity changes here - the same "destroy and
        // recreate" pattern already tested and confirmed working for join/split (see the auto-
        // join loser handling above and JoinOrSplitJoycon). A controller whose PadId doesn't
        // change is left completely untouched - no churn, no risk to a game already using it.
        //
        // A joined pair shares one rank between both halves for LED display, matching
        // Joycon.other's setter (which also does Math.Min(...) between a pair to pick one LED
        // value for both) - but only the pair's active half (the one actually holding a virtual
        // controller) goes through AssignPadId; the passive half is deliberately left without a
        // virtual controller (see the auto-join/JoinOrSplitJoycon loser handling) and must stay
        // that way, or a joined pair goes back to showing up as two separate XInputs.
        //
        // Critically, the passive half's actual PadId field is left completely untouched here -
        // only its LED is refreshed. It already has its own distinct PadId from whenever it was
        // originally connected (join never touches PadId, only out_xbox/out_ds4), parked and
        // waiting for whenever this pair splits back apart. Overwriting it to match the active
        // half's compacted rank - as an earlier version of this method did - made both halves
        // share the same PadId, so splitting the pair later produced two controllers that both
        // claimed to be the same player instead of two distinct ones.
        void ReassignPadIds() {
            var ranked = new List<Joycon>(j);
            ranked.Sort((a, b) => a.PadId.CompareTo(b.PadId));

            var assigned = new HashSet<Joycon>();
            int rank = 0;
            foreach (Joycon jc in ranked) {
                if (assigned.Contains(jc))
                    continue;

                assigned.Add(jc);
                bool isPair = jc.other != null && jc.other != jc;
                if (isPair)
                    assigned.Add(jc.other);

                Joycon active = jc;
                Joycon passive = null;
                if (isPair) {
                    bool jcHasOutput = jc.out_xbox != null || jc.out_ds4 != null;
                    active = jcHasOutput ? jc : jc.other;
                    passive = active == jc ? jc.other : jc;
                }

                AssignPadId(active, rank);
                if (passive != null)
                    passive.RequestLEDUpdate(rank);

                rank++;
            }
        }

        void AssignPadId(Joycon jc, int newPadId) {
            if (jc.PadId == newPadId)
                return;

            jc.PadId = newPadId;
            jc.RequestLEDUpdate(newPadId);

            if (jc.out_xbox != null) {
                try { jc.out_xbox.Disconnect(); } catch { }
                jc.out_xbox = null;
            }
            if (jc.out_ds4 != null) {
                try { jc.out_ds4.Disconnect(); } catch { }
                jc.out_ds4 = null;
            }
            CreateOutputControllers(jc);
        }

        // Shared by AssignPadId and CleanUp's survivor-restore, both of which need a freshly
        // Connect()ed virtual controller built from the same config-driven settings. No-op for
        // whichever half(s) already have one.
        void CreateOutputControllers(Joycon jc) {
            if (Boolean.Parse(ConfigurationManager.AppSettings["ShowAsXInput"]) && jc.out_xbox == null) {
                jc.out_xbox = new Controller.OutputControllerXbox360();
                if (Boolean.Parse(ConfigurationManager.AppSettings["EnableRumble"]))
                    jc.out_xbox.FeedbackReceived += jc.ReceiveRumble;
                jc.out_xbox.Connect();
            }
            if (Boolean.Parse(ConfigurationManager.AppSettings["ShowAsDS4"]) && jc.out_ds4 == null) {
                jc.out_ds4 = new Controller.OutputControllerDualShock4();
                if (Boolean.Parse(ConfigurationManager.AppSettings["EnableRumble"]))
                    jc.out_ds4.FeedbackReceived += jc.Ds4_FeedbackReceived;
                jc.out_ds4.Connect();
            }
        }

        void CheckForNewControllersTime(Object source, ElapsedEventArgs e) {
            lock (scanLock) {
                if (scanningStopped)
                    return;

                CleanUp();
                if (Boolean.Parse(ConfigurationManager.AppSettings["PassiveScan"])) {
                    CheckForNewControllers();
                }
            }
        }

        // Lets a caller outside the scan loop (see HeadlessJoyconHost's config-file watcher)
        // mutate state a scan pass reads - e.g. Program.thirdPartyCons/blacklistedCons via
        // _3rdPartyControllers.LoadIntoProgramLists - without racing CheckForNewControllers'
        // own iteration over them. Those are plain Lists, not ConcurrentList, so a Clear()+
        // AddRange() from another thread while a scan enumerates them could throw "Collection
        // was modified" or hand device classification a half-rebuilt list.
        public void RunExclusiveOfScanning(Action action) {
            lock (scanLock) {
                action();
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
                    lock (Program.hiddenInstanceIdsLock) {
                        Program.hiddenInstanceIds.Add(instanceId);
                    }
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

                // BetterJoy's own virtual output, mistaken for a brand new physical controller -
                // see the constants above. Unlike the blacklist case below, deliberately NOT
                // hidden via HidHide: other programs (games, Steam) are supposed to see this as a
                // normal Xbox360/DS4 controller, that's the entire point of it existing.
                if ((enumerate.vendor_id == vigemXbox360VendorId && enumerate.product_id == vigemXbox360ProductId) ||
                    (enumerate.vendor_id == vigemDs4VendorId && enumerate.product_id == vigemDs4ProductId)) {
                    ptr = enumerate.next;
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
                    // j.Count (list size, not a stable slot) duplicates an existing PadId the
                    // moment a middle controller disconnects and a new one connects afterward -
                    // e.g. with PadIds 0/1/2 connected, 1 drops, the next new controller would
                    // also get j.Count == 2, colliding with the controller still holding it.
                    // Remote-mode commands (TestRumble/JoinOrSplit/StartCalibration) resolve a
                    // controller by PadId alone, so a collision could route a command to the
                    // wrong physical controller, not just misrender a GUI slot.
                    j.Add(new Joycon(handle, EnableIMU, EnableLocalize & EnableIMU, 0.05f, isLeft, enumerate.path, enumerate.serial_number, NextAvailablePadId(), isPro, isSnes, is64,thirdParty != null));

                    foundNew = true;
                    j.Last().form = form;
                    form.AssignSlot(j.Last());

                    byte[] mac = new byte[6];
                    try {
                        for (int n = 0; n < 6; n++)
                            mac[n] = byte.Parse(enumerate.serial_number.Substring(n * 2, 2), System.Globalization.NumberStyles.HexNumber);
                    } catch (Exception) {
                        // could not parse mac address
                    }
                    j[j.Count - 1].PadMacAddress = new PhysicalAddress(mac);
                }

                ptr = enumerate.next;
            }

            HIDapi.hid_free_enumeration(top_ptr);

            // Connect/attach every newly-found device BEFORE auto-join runs below. Auto-join's
            // pairing (Joycon.other's setter) sends a player-LED subcommand immediately, which
            // needs the physical controller to have already been through its handshake (Attach)
            // to respond correctly. Pairing a controller that just connected this same pass (not
            // yet attached) with one left over from an earlier pass (already attached) used to
            // send that LED command before the fresh one was ready for it - silently dropped, or
            // leaving its protocol state confused. Reliably reproduced: whichever Joycon happened
            // to connect second kept showing its own solo player number instead of the pair's,
            // and the pair's virtual controller didn't actually work in games afterward even
            // though it looked fine in joy.cpl.
            bool on = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None).AppSettings.Settings["HomeLEDOn"].Value.ToLower() == "true";
            foreach (Joycon jc in j) { // Connect device straight away
                if (jc.state == Joycon.state_.NOT_ATTACHED) {
                    if (jc.out_xbox != null)
                        jc.out_xbox.Connect();
                    if (jc.out_ds4 != null)
                        jc.out_ds4.Connect();

                    try {
                        jc.Attach();
                    } catch (Exception) {
                        jc.state = Joycon.state_.DROPPED;
                        continue;
                    }

                    // USB enumeration exposes a shared placeholder serial; Attach resolves the
                    // controller's real MAC. Give service-mode clients a fresh profile identity
                    // before button polling begins, rather than leaving the dropdown on the
                    // temporary HID-path identity sent when the slot was first discovered.
                    form.RefreshControllerState();

                    jc.SetHomeLight(on);

                    jc.Begin();
                    if (Boolean.Parse(ConfigurationManager.AppSettings["AllowCalibration"])) {
                        jc.getActiveData();
                    }
                }
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

                        // Disconnect whichever controller was created later - see Joycon.
                        // virtualControllerSequence - not just whichever happened to be found
                        // first in this iteration (usually but not guaranteed to be the older
                        // one). Its controller was just Connect()ed moments ago by the "connect
                        // device straight away" loop above, so this is a real disconnect, not a
                        // no-op - matches a real unplug (clean, no leftover state); the other one
                        // is left untouched as the pair's shared controller.
                        Joycon loser = temp.virtualControllerSequence > v.virtualControllerSequence ? temp : v;
                        if (loser.out_xbox != null) {
                            try { loser.out_xbox.Disconnect(); } catch { }
                            loser.out_xbox = null;
                        }
                        if (loser.out_ds4 != null) {
                            try { loser.out_ds4.Disconnect(); } catch { }
                            loser.out_ds4 = null;
                        }

                        Joycon left = temp.isLeft ? temp : v;
                        Joycon right = temp.isLeft ? v : temp;
                        form.CollapseJoinedPair(left, right);

                        temp = null;    // repeat
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

            StopScanning();
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

        // Guards hiddenInstanceIds - a plain List<string> accessed from both the scan timer's
        // background thread (JoyconManager.TryHideController, adding) and Stop() below
        // (enumerating + clearing) on whatever thread calls it. Stopping the scan timer first
        // (see StopScanning) closes most of the window but doesn't wait for a callback already
        // in flight to finish - this lock is what actually guarantees no concurrent mutation.
        public static readonly object hiddenInstanceIdsLock = new object();

        public static List<SController> thirdPartyCons = new List<SController>();
        public static List<SController> blacklistedCons = new List<SController>();

        private static WindowsInput.Events.Sources.IKeyboardEventSource keyboard;
        private static WindowsInput.Events.Sources.IMouseEventSource mouse;

        public static void Start() {
            // Previously only ever called from MainForm_Load, so a Windows Service (which never
            // constructs a MainForm) silently never loaded remap keybinds (capture/home/sl_*/
            // sr_*/shake/reset_mouse/active_gyro) at all - every Config.Value(...) lookup for
            // them returned "" under service mode. Moved here so both modes get it.
            Config.Init(CalibrationState.CaliData, CalibrationState.StickCaliData, CalibrationState.Stick2CaliData);

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

        // reset_mouse/active_gyro used to be decided here (single key_/mse_ trigger only), but
        // now that both support a "+"-joined combo mixing controller/keyboard/mouse inputs
        // together (see Joycon.IsComboHeld), they need simultaneous visibility into all three at
        // once - evaluated once per packet in Joycon.DoThingsWithButtons instead, which already
        // runs per connected controller and can check its own buttons directly. These handlers
        // now just feed InputState so that check has an up-to-date view of which keys/mouse
        // buttons are currently held - kept independent of *how* the raw event was observed, so
        // both GUI mode's direct WindowsInput.Capture.Global hook and service mode's pipe-
        // forwarded events from the session-launched helper feed the exact same tracker.
        public static void OnKeyDown(int keyCode) => InputState.KeyDown(keyCode);
        public static void OnKeyUp(int keyCode) => InputState.KeyUp(keyCode);
        public static void OnMouseButtonDown(int buttonCode) => InputState.MouseDown(buttonCode);
        public static void OnMouseButtonUp(int buttonCode) => InputState.MouseUp(buttonCode);

        public static void Stop() {
            // Stop the background scan first - otherwise it can still be adding to
            // hiddenInstanceIds (TryHideController) while the loop below enumerates/clears it.
            mgr.StopScanning();

            if (useHidHide && hidHide != null && Boolean.Parse(ConfigurationManager.AppSettings["UnhideOnExit"])) {
                lock (hiddenInstanceIdsLock) {
                    foreach (string id in hiddenInstanceIds) {
                        try { hidHide.RemoveBlockedInstanceId(id); } catch { }
                    }
                    hiddenInstanceIds.Clear();
                }
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
