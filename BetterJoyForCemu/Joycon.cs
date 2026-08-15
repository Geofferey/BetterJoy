using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using BetterJoyForCemu.Controller;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace BetterJoyForCemu {
    public class Joycon {
        public string path = String.Empty;
        public bool isPro = false;
        public bool isSnes = false;
        public bool is64 = false;
        bool isUSB = false;
        private Joycon _other = null;

        // 64 vars
        float maxX = 0.5f;
        float minX = -0.5f;
        float maxY = 0.5f;
        float minY = -0.5f;

        public Joycon other {
            get {
                return _other;
            }
            set {
                _other = value;

                // Queued (RequestLEDUpdate), not written directly - this setter runs on
                // whatever thread is doing the join/split (scan thread for auto-join, UI/pipe
                // thread for a manual one), which by this point always races this Joycon's own
                // already-running Poll() thread for the HID handle. See RequestLEDUpdate's
                // comment.
                if (_other == null || _other == this) {
                    // If the other Joycon is itself, the Joycon is sideways - LED to current PadId
                    RequestLEDUpdate(PadId);
                } else {
                    // Set LED to current Joycon Pair
                    int lowestPadId = Math.Min(_other.PadId, PadId);
                    RequestLEDUpdate(lowestPadId);
                }
            }
        }
        public bool active_gyro = false;

        // Real elapsed time since the last DoThingsWithButtons call, used to scale raw angular
        // velocity (gyr_g) into a per-packet rotation amount - previously a hardcoded 0.015f
        // (assumed 15ms/~66Hz) regardless of how much time had actually passed. Report timing
        // isn't perfectly metronomic, especially over Bluetooth, so a fixed assumption scales
        // every frame's motion by however wrong that assumption happened to be that frame -
        // read as jittery/inconsistent speed rather than smooth motion, independent of anything
        // else about the connection or IMU filtering settings. -1 the first call so a long gap
        // before the very first packet (e.g. right after connecting) can't produce a huge dt.
        private long lastDoThingsTimestamp = -1;

        // Tracks the active_gyro combo's held state from the previous packet, so toggle mode
        // (GyroHoldToggle == false) can flip on the rising edge only - the moment the combo
        // first becomes fully held, not every packet it stays held.
        private bool prevActiveGyroComboHeld = false;

        // Same idea for reset_mouse - a one-shot action needs the rising edge only, or it would
        // keep re-centering every packet for as long as the bind stays held.
        private bool prevResetMouseComboHeld = false;

        // A bind is one or more "joy_N"/"key_N"/"mse_N" parts joined with "+" (a single part is
        // just a combo of one) - true only when every part is currently held at once. Controller
        // parts check this Joycon's own buttons (and its pair partner's, if joined, matching how
        // every other joy_ bind here already treats a pair as one logical controller);
        // keyboard/mouse parts check InputState, fed from Program.OnKeyDown/OnKeyUp/
        // OnMouseButtonDown/OnMouseButtonUp - the same unified entry points that already work in
        // both GUI and service mode.
        private bool IsComboHeld(string combo) {
            foreach (string part in combo.Split('+')) {
                if (part.StartsWith("joy_")) {
                    int i = Int32.Parse(part.Substring(4));
                    if (!(buttons[i] || (other != null && other != this && other.buttons[i])))
                        return false;
                } else if (part.StartsWith("key_")) {
                    if (!InputState.IsKeyHeld(Int32.Parse(part.Substring(4))))
                        return false;
                } else if (part.StartsWith("mse_")) {
                    if (!InputState.IsMouseButtonHeld(Int32.Parse(part.Substring(4))))
                        return false;
                } else {
                    return false; // malformed/unknown part - fail closed rather than ignore it
                }
            }
            return true;
        }

        private long inactivity = Stopwatch.GetTimestamp();

        public bool send = true;

        public enum DebugType : int {
            NONE,
            ALL,
            COMMS,
            THREADING,
            IMU,
            RUMBLE,
            SHAKE,
            STICK, // appended, not inserted - existing numeric values are persisted in App.config/settings
        };
        public DebugType debug_type = (DebugType)int.Parse(ConfigurationManager.AppSettings["DebugType"]);
        //public DebugType debug_type = DebugType.NONE; //Keep this for manual debugging during development.
        public bool isLeft;
        public enum state_ : uint {
            NOT_ATTACHED,
            DROPPED,
            NO_JOYCONS,
            ATTACHED,
            INPUT_MODE_0x30,
            IMU_DATA_OK,
        };
        public state_ state;
        public enum Button : int {
            DPAD_DOWN = 0,
            DPAD_RIGHT = 1,
            DPAD_LEFT = 2,
            DPAD_UP = 3,
            SL = 4,
            SR = 5,
            MINUS = 6,
            HOME = 7,
            PLUS = 8,
            CAPTURE = 9,
            STICK = 10,
            SHOULDER_1 = 11,
            SHOULDER_2 = 12,

            // For pro controller
            B = 13,
            A = 14,
            Y = 15,
            X = 16,
            STICK2 = 17,
            SHOULDER2_1 = 18,
            SHOULDER2_2 = 19,
        };
        private bool[] buttons_down = new bool[20];
        private bool[] buttons_up = new bool[20];
        private bool[] buttons = new bool[20];
        private bool[] down_ = new bool[20];
        private long[] buttons_down_timestamp = new long[20];

        private float[] stick = { 0, 0 };
        private float[] stick2 = { 0, 0 };

        private IntPtr handle;

        byte[] default_buf = { 0x0, 0x1, 0x40, 0x40, 0x0, 0x1, 0x40, 0x40 };

        private byte[] stick_raw = { 0, 0, 0 };
        private UInt16[] stick_cal = { 0, 0, 0, 0, 0, 0 };
        private UInt16 deadzone;
        private UInt16[] stick_precal = { 0, 0 };

        private byte[] stick2_raw = { 0, 0, 0 };
        private UInt16[] stick2_cal = { 0, 0, 0, 0, 0, 0 };
        private UInt16 deadzone2;
        private UInt16[] stick2_precal = { 0, 0 };

        private bool stop_polling = true;
        private bool imu_enabled = false;
        private Int16[] acc_r = { 0, 0, 0 };
        private Int16[] acc_neutral = { 0, 0, 0 };
        private Int16[] acc_sensiti = { 0, 0, 0 };
        private Vector3 acc_g;

        private Int16[] gyr_r = { 0, 0, 0 };
        private Int16[] gyr_neutral = { 0, 0, 0 };
        private Int16[] gyr_sensiti = { 0, 0, 0 };
        private Vector3 gyr_g;

        private float[] cur_rotation; // Filtered IMU data

        private short[] acc_sen = new short[3]{
            16384,
            16384,
            16384
        };
        private short[] gyr_sen = new short[3]{
            18642,
            18642,
            18642
        };

        private Int16[] pro_hor_offset = { -710, 0, 0 };
        private Int16[] left_hor_offset = { 0, 0, 0 };
        private Int16[] right_hor_offset = { 0, 0, 0 };

        private bool do_localize;
        private float filterweight;
        private const uint report_len = 49;

        private struct Rumble {
            public Queue<float[]> queue;

            public void set_vals(float low_freq, float high_freq, float amplitude) {
                float[] rumbleQueue = new float[] { low_freq, high_freq, amplitude };
                // Keep a queue of 15 items, discard oldest item if queue is full.
                if (queue.Count > 15) {
                    queue.Dequeue();
                }
                queue.Enqueue(rumbleQueue);
            }
            public Rumble(float[] rumble_info) {
                queue = new Queue<float[]>();
                queue.Enqueue(rumble_info);
            }
            private float clamp(float x, float min, float max) {
                if (x < min) return min;
                if (x > max) return max;
                return x;
            }

            private byte EncodeAmp(float amp) {
                byte en_amp;

                if (amp == 0)
                    en_amp = 0;
                else if (amp < 0.117)
                    en_amp = (byte)(((Math.Log(amp * 1000, 2) * 32) - 0x60) / (5 - Math.Pow(amp, 2)) - 1);
                else if (amp < 0.23)
                    en_amp = (byte)(((Math.Log(amp * 1000, 2) * 32) - 0x60) - 0x5c);
                else
                    en_amp = (byte)((((Math.Log(amp * 1000, 2) * 32) - 0x60) * 2) - 0xf6);

                return en_amp;
            }

            public byte[] GetData() {
                byte[] rumble_data = new byte[8];
                float[] queued_data = queue.Dequeue();

                if (queued_data[2] == 0.0f) {
                    rumble_data[0] = 0x0;
                    rumble_data[1] = 0x1;
                    rumble_data[2] = 0x40;
                    rumble_data[3] = 0x40;
                } else {
                    queued_data[0] = clamp(queued_data[0], 40.875885f, 626.286133f);
                    queued_data[1] = clamp(queued_data[1], 81.75177f, 1252.572266f);

                    queued_data[2] = clamp(queued_data[2], 0.0f, 1.0f);

                    UInt16 hf = (UInt16)((Math.Round(32f * Math.Log(queued_data[1] * 0.1f, 2)) - 0x60) * 4);
                    byte lf = (byte)(Math.Round(32f * Math.Log(queued_data[0] * 0.1f, 2)) - 0x40);
                    byte hf_amp = EncodeAmp(queued_data[2]);

                    UInt16 lf_amp = (UInt16)(Math.Round((double)hf_amp) * .5);
                    byte parity = (byte)(lf_amp % 2);
                    if (parity > 0) {
                        --lf_amp;
                    }

                    lf_amp = (UInt16)(lf_amp >> 1);
                    lf_amp += 0x40;
                    if (parity > 0) lf_amp |= 0x8000;

                    hf_amp = (byte)(hf_amp - (hf_amp % 2)); // make even at all times to prevent weird hum
                    rumble_data[0] = (byte)(hf & 0xff);
                    rumble_data[1] = (byte)(((hf >> 8) & 0xff) + hf_amp);
                    rumble_data[2] = (byte)(((lf_amp >> 8) & 0xff) + lf);
                    rumble_data[3] = (byte)(lf_amp & 0xff);
                }

                for (int i = 0; i < 4; ++i) {
                    rumble_data[4 + i] = rumble_data[i];
                }

                return rumble_data;
            }
        }

        private Rumble rumble_obj;

        private byte global_count = 0;

        // For UdpServer
        public int PadId = 0;
        public int battery = -1;
        public int model = 2;
        public int constate = 2;
        public int connection = 3;

        public PhysicalAddress PadMacAddress = new PhysicalAddress(new byte[] { 01, 02, 03, 04, 05, 06 });
        public ulong Timestamp = 0;
        public int packetCounter = 0;

        public OutputControllerXbox360 out_xbox;
        public OutputControllerDualShock4 out_ds4;

        // Monotonic creation order, assigned once in the constructor - used to decide which half
        // of a pair gets disconnected on join: whichever connected (and got its virtual
        // controller created) FIRST is the one most likely to already be the controller a
        // running game has locked onto, so joining always disconnects the newer one and keeps
        // the older one active, regardless of which physical Joycon you click to initiate the
        // join or which one a scan pass happens to enumerate first. Confirmed by testing:
        // disconnecting/suppressing the wrong half left
        // a game's already-locked-on slot silent while input went to a different slot it wasn't
        // watching.
        private static long nextVirtualControllerSequence = 0;
        public readonly long virtualControllerSequence = System.Threading.Interlocked.Increment(ref nextVirtualControllerSequence);

        int lowFreq = Int32.Parse(ConfigurationManager.AppSettings["LowFreqRumble"]);
        int highFreq = Int32.Parse(ConfigurationManager.AppSettings["HighFreqRumble"]);

        bool toRumble = Boolean.Parse(ConfigurationManager.AppSettings["EnableRumble"]);

        bool showAsXInput = Boolean.Parse(ConfigurationManager.AppSettings["ShowAsXInput"]);
        bool showAsDS4 = Boolean.Parse(ConfigurationManager.AppSettings["ShowAsDS4"]);

        public IJoyconHost form;

        public byte LED { get; private set; } = 0x0;
        public void SetLEDByPlayerNum(int id) {
            if (id > 3) {
                // No support for any higher than 3 (4 Joycons/Controllers supported in the application normally)
                id = 3;
            }

            if (ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None).AppSettings.Settings["UseIncrementalLights"].Value.ToLower() == "true") {
                // Set all LEDs from 0 to the given id to lit
                int ledId = id;
                LED = 0x0;
                do {
                    LED |= (byte)(0x1 << ledId);
                } while (--ledId >= 0);
            } else {
                LED = (byte)(0x1 << id);
            }

            SetPlayerLED(LED);
        }

        public string serial_number;

        // True for anything BetterJoy attached because it matched the 3rd-party-controller
        // allowlist (Program.thirdPartyCons - see CheckForNewControllers), rather than being a
        // real Joy-Con/Pro/SNES/N64 device matched by VID/PID directly. Notably, this also
        // catches BetterJoy's OWN virtual XInput/DS4 output controller getting misidentified as a
        // new physical controller when AutoAddControllers is on - Windows exposes ViGEmBus's
        // emulated pad through a HID interface too (for DirectInput compatibility), which passes
        // the same generic "is this a gamepad" usage-page/usage check real third-party controllers
        // do. Left attached (it may still be something the user genuinely wants passthrough for),
        // but button-mapping detection (Reassign.cs/HeadlessJoyconHost.cs) excludes it - otherwise
        // every physical press doubles into a second "press" mirrored from the virtual pad.
        public bool thirdParty = false;

        private float[] activeData;
        static float AHRS_beta = float.Parse(ConfigurationManager.AppSettings["AHRS_beta"]);
        private MadgwickAHRS AHRS = new MadgwickAHRS(0.005f, AHRS_beta); // for getting filtered Euler angles of rotation; 5ms sampling rate

        public Joycon(IntPtr handle_, bool imu, bool localize, float alpha, bool left, string path, string serialNum, int id = 0, bool isPro = false, bool isSnes = false, bool is64 = false, bool thirdParty = false) {
            serial_number = serialNum;
            activeData = new float[6];
            handle = handle_;
            imu_enabled = imu;
            do_localize = localize;
            rumble_obj = new Rumble(new float[] { lowFreq, highFreq, 0 });
            for (int i = 0; i < buttons_down_timestamp.Length; i++)
                buttons_down_timestamp[i] = -1;
            filterweight = alpha;
            isLeft = left;

            PadId = id;
            LED = (byte)(0x1 << PadId);
            this.isPro = isPro || isSnes || is64;
            this.isSnes = isSnes;
            this.is64 = is64;
            isUSB = serialNum == "000000000001";
            this.thirdParty = thirdParty;

            this.path = path;

            connection = isUSB ? 0x01 : 0x02;

            if (showAsXInput) {
                out_xbox = new OutputControllerXbox360();
                if (toRumble)
                    out_xbox.FeedbackReceived += ReceiveRumble;
            }

            if (showAsDS4) {
                out_ds4 = new OutputControllerDualShock4();
                if (toRumble)
                    out_ds4.FeedbackReceived += Ds4_FeedbackReceived;
            }
        }

        public void getActiveData() {
            this.activeData = CalibrationState.ActiveCaliData(serial_number);
        }

        // Applies any empirically-recalibrated stick data on top of whatever dump_calibration_data
        // already loaded from SPI - called both there (so a controller with prior stick
        // recalibration gets it immediately on every future connect, not just the session it was
        // captured in) and right after CalibrationState.FinishStickCalibration (so it takes effect
        // without needing a reconnect). No-op per stick when ActiveStickCal returns null - i.e.
        // this controller/stick has never been recalibrated, so the SPI-read values stand.
        public void getActiveStickData() {
            ushort[] primary = CalibrationState.ActiveStickCal(serial_number, false);
            if (primary != null) {
                Array.Copy(primary, stick_cal, 6);
                PrintArray(stick_cal, DebugType.STICK, len: 6, start: 0, format: "Applied recalibrated stick data: {0:S}");
            }

            if (isPro) {
                ushort[] secondary = CalibrationState.ActiveStickCal(serial_number, true);
                if (secondary != null) {
                    Array.Copy(secondary, stick2_cal, 6);
                    PrintArray(stick2_cal, DebugType.STICK, len: 6, start: 0, format: "Applied recalibrated stick2 data: {0:S}");
                }
            }
        }

        public void ReceiveRumble(Xbox360FeedbackReceivedEventArgs e) {
            DebugPrint("Rumble data Recived: XInput", DebugType.RUMBLE);
            SetRumble(lowFreq, highFreq, (float)Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);

            if (other != null && other != this)
                other.SetRumble(lowFreq, highFreq, (float)Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);
        }

        public void Ds4_FeedbackReceived(DualShock4FeedbackReceivedEventArgs e) {
            DebugPrint("Rumble data Recived: DS4", DebugType.RUMBLE);
            SetRumble(lowFreq, highFreq, (float)Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);

            if (other != null && other != this)
                other.SetRumble(lowFreq, highFreq, (float)Math.Max(e.LargeMotor, e.SmallMotor) / (float)255);
        }

        public void DebugPrint(String s, DebugType d) {
            if (debug_type == DebugType.NONE) return;
            if (d == DebugType.ALL || d == debug_type || debug_type == DebugType.ALL) {
                form.AppendTextBox(s + "\r\n");
            }
        }
        public bool GetButtonDown(Button b) {
            return buttons_down[(int)b];
        }
        public bool GetButton(Button b) {
            return buttons[(int)b];
        }
        public bool GetButtonUp(Button b) {
            return buttons_up[(int)b];
        }
        public float[] GetStick() {
            return stick;
        }
        public float[] GetStick2() {
            return stick2;
        }
        public Vector3 GetGyro() {
            return gyr_g;
        }
        public Vector3 GetAccel() {
            return acc_g;
        }
        public int Attach() {
            state = state_.ATTACHED;

            // Make sure command is received
            HIDapi.hid_set_nonblocking(handle, 0);

            byte[] a = { 0x0 };

            // Connect
            if (isUSB) {
                a = Enumerable.Repeat((byte)0, 64).ToArray();
                form.AppendTextBox("Using USB.\r\n");

                a[0] = 0x80;
                a[1] = 0x1;
                HIDapi.hid_write(handle, a, new UIntPtr(2));
                HIDapi.hid_read_timeout(handle, a, new UIntPtr(64), 100);

                if (a[0] != 0x81) { // can occur when USB connection isn't closed properly
                    form.AppendTextBox("Resetting USB connection.\r\n");
                    Subcommand(0x06, new byte[] { 0x01 }, 1);
                    throw new Exception("reset_usb");
                }

                if (a[3] == 0x3) {
                    PadMacAddress = new PhysicalAddress(new byte[] { a[9], a[8], a[7], a[6], a[5], a[4] });
                }

                // USB Pairing
                a = Enumerable.Repeat((byte)0, 64).ToArray();
                a[0] = 0x80; a[1] = 0x2; // Handshake
                HIDapi.hid_write(handle, a, new UIntPtr(2));
                HIDapi.hid_read_timeout(handle, a, new UIntPtr(64), 100);

                a[0] = 0x80; a[1] = 0x3; // 3Mbit baud rate
                HIDapi.hid_write(handle, a, new UIntPtr(2));
                HIDapi.hid_read_timeout(handle, a, new UIntPtr(64), 100);

                a[0] = 0x80; a[1] = 0x2; // Handshake at new baud rate
                HIDapi.hid_write(handle, a, new UIntPtr(2));
                HIDapi.hid_read_timeout(handle, a, new UIntPtr(64), 100);

                a[0] = 0x80; a[1] = 0x4; // Prevent HID timeout
                HIDapi.hid_write(handle, a, new UIntPtr(2)); // doesn't actually prevent timout...
                HIDapi.hid_read_timeout(handle, a, new UIntPtr(64), 100);

            }
            dump_calibration_data();

            // Bluetooth manual pairing
            byte[] btmac_host = Program.btMAC.GetAddressBytes();
            // send host MAC and acquire Joycon MAC
            //byte[] reply = Subcommand(0x01, new byte[] { 0x01, btmac_host[5], btmac_host[4], btmac_host[3], btmac_host[2], btmac_host[1], btmac_host[0] }, 7, true);
            //byte[] LTKhash = Subcommand(0x01, new byte[] { 0x02 }, 1, true);
            // save pairing info
            //Subcommand(0x01, new byte[] { 0x03 }, 1, true);

            BlinkHomeLight();
            SetLEDByPlayerNum(PadId);

            Subcommand(0x40, new byte[] { (imu_enabled ? (byte)0x1 : (byte)0x0) }, 1);
            Subcommand(0x48, new byte[] { 0x01 }, 1);

            Subcommand(0x3, new byte[] { 0x30 }, 1);
            DebugPrint("Done with init.", DebugType.COMMS);

            HIDapi.hid_set_nonblocking(handle, 1);

            return 0;
        }

        public void SetPlayerLED(byte leds_ = 0x0) {
            Subcommand(0x30, new byte[] { leds_ }, 1);
        }

        public void BlinkHomeLight() { // do not call after initial setup
            if (thirdParty)
                return;
            byte[] a = Enumerable.Repeat((byte)0xFF, 25).ToArray();
            a[0] = 0x18;
            a[1] = 0x01;
            Subcommand(0x38, a, 25);
        }

        public void SetHomeLight(bool on) {
            if (thirdParty)
                return;
            byte[] a = Enumerable.Repeat((byte)0xFF, 25).ToArray();
            if (on) {
                a[0] = 0x1F;
                a[1] = 0xF0;
            } else {
                a[0] = 0x10;
                a[1] = 0x01;
            }
            Subcommand(0x38, a, 25);
        }

        private void SetHCIState(byte state) {
            byte[] a = { state };
            Subcommand(0x06, a, 1);
        }

        public void PowerOff() {
            if (state > state_.DROPPED) {
                HIDapi.hid_set_nonblocking(handle, 0);
                SetHCIState(0x00);
                state = state_.DROPPED;
            }
        }

        // Shared with MainForm.AssignJoyconToSlot, which needs to apply an already-known
        // battery level immediately when a Joycon claims a slot (e.g. splitting off a
        // collapsed pair) - otherwise that slot would show no battery color at all until the
        // next battery-level event happens to fire.
        public static System.Drawing.Color GetBatteryColor(int battery) {
            switch (battery) {
                case 4:
                case 3:
                    return System.Drawing.Color.FromArgb(0xAA, System.Drawing.Color.Green);
                case 2:
                    return System.Drawing.Color.FromArgb(0xAA, System.Drawing.Color.GreenYellow);
                case 1:
                    return System.Drawing.Color.FromArgb(0xAA, System.Drawing.Color.Orange);
                default:
                    return System.Drawing.Color.FromArgb(0xAA, System.Drawing.Color.Red);
            }
        }

        // Called once this controller (Joycon, Pro, SNES, or N64 - all share this class) has
        // actually confirmed itself alive (see retiredDuplicates in Poll()). Attach() resolves
        // the real per-unit MAC address (for a USB connection this is only known once the USB
        // handshake completes - the HID enumeration serial number USB reports is just a shared
        // placeholder, not the real MAC). If another already-connected entry turns out to be
        // this same physical controller over a different transport (e.g. it was connected
        // wirelessly and has now been plugged in via USB), retire that stale entry now rather
        // than waiting for its own poll thread to notice the connection went silent - that
        // window otherwise leaves both the old and new entries live at once, each driving their
        // own virtual output device (double presses/duplicate input in games).
        private void RetireDuplicateConnections() {
            foreach (Joycon other in Program.mgr.j) {
                if (other != this && other.state != state_.DROPPED && other.PadMacAddress.Equals(PadMacAddress)) {
                    other.state = state_.DROPPED;
                    form.AppendTextBox("Retiring duplicate connection for the same controller.\r\n");
                }
            }
        }

        private void BatteryChanged() { // battery changed level
            form.UpdateBatteryColor(this);

            if (battery <= 1 && !isUSB) {
                form.NotifyLowBattery(this);
            }
        }

        public void SetFilterCoeff(float a) {
            filterweight = a;
        }

        public void Detach(bool close = false) {
            stop_polling = true;

            if (out_xbox != null) {
                out_xbox.Disconnect();
            }

            if (out_ds4 != null) {
                out_ds4.Disconnect();
            }

            if (state > state_.NO_JOYCONS) {
                HIDapi.hid_set_nonblocking(handle, 0);

                // Subcommand(0x40, new byte[] { 0x0 }, 1); // disable IMU sensor
                //Subcommand(0x48, new byte[] { 0x0 }, 1); // Would turn off rumble?

                if (isUSB) {
                    byte[] a = Enumerable.Repeat((byte)0, 64).ToArray();
                    a[0] = 0x80; a[1] = 0x5; // Allow device to talk to BT again
                    HIDapi.hid_write(handle, a, new UIntPtr(2));
                    a[0] = 0x80; a[1] = 0x6; // Allow device to talk to BT again
                    HIDapi.hid_write(handle, a, new UIntPtr(2));
                }
            }
            if (close || state > state_.DROPPED) {
                HIDapi.hid_close(handle);
            }
            state = state_.NOT_ATTACHED;
        }

        private byte ts_en;

        // An occasional duplicate timestamp is normal (we can poll faster than the device
        // produces new reports); a run of them is not - it means the report stream has
        // genuinely stalled, which happens when another program (e.g. Steam) grabbed the raw
        // device before HidHide had a chance to hide it (a real race on a fresh boot/cleared
        // settings, since HidHide's hidden-device list isn't there yet to fall back on and
        // Steam may already be running). Detaching and letting the normal reconnect flow
        // (CleanUp -> rediscovered on the next scan) pick it back up clears the stall in
        // practice, so treat a short run the same as a connection-loss timeout.
        private int duplicateTimestampCount = 0;
        private const int MaxConsecutiveDuplicateTimestamps = 3;

        private int ReceiveRaw() {
            if (handle == IntPtr.Zero) return -2;
            byte[] raw_buf = new byte[report_len];
            int ret = HIDapi.hid_read_timeout(handle, raw_buf, new UIntPtr(report_len), 5);

            if (ret > 0) {
                // Process packets as soon as they come
                for (int n = 0; n < 3; n++) {
                    ExtractIMUValues(raw_buf, n);

                    byte lag = (byte)Math.Max(0, raw_buf[1] - ts_en - 3);
                    if (n == 0) {
                        Timestamp += (ulong)lag * 5000; // add lag once
                        ProcessButtonsAndStick(raw_buf);

                        // process buttons here to have them affect DS4
                        DoThingsWithButtons();

                        int newbat = battery;
                        battery = (raw_buf[2] >> 4) / 2;
                        if (newbat != battery)
                            BatteryChanged();
                    }
                    ProcessGyroMouseSample(n == 2);
                    Timestamp += 5000; // 5ms difference

                    packetCounter++;
                    if (Program.server != null)
                        Program.server.NewReportIncoming(this);

                    if (out_ds4 != null) {
                        try {
                            out_ds4.UpdateInput(MapToDualShock4Input(this));
                        } catch (Exception) {
                            // ignore /shrug
                        }
                    }
                }

                // no reason to send XInput reports so often
                if (out_xbox != null) {
                    try {
                        out_xbox.UpdateInput(MapToXbox360Input(this));
                    } catch (Exception) {
                        // ignore /shrug
                    }
                }


                if (ts_en == raw_buf[1] && !(isSnes || is64)) {
                    form.AppendTextBox("Duplicate timestamp enqueued.\r\n");
                    DebugPrint(string.Format("Duplicate timestamp enqueued. TS: {0:X2}", ts_en), DebugType.THREADING);

                    duplicateTimestampCount++;
                    if (duplicateTimestampCount >= MaxConsecutiveDuplicateTimestamps) {
                        form.AppendTextBox("Report stream stalled (another program may have grabbed this controller before it was hidden) - reattaching to recover.\r\n");
                        duplicateTimestampCount = 0;
                        state = state_.DROPPED;
                    }
                } else {
                    duplicateTimestampCount = 0;
                }
                ts_en = raw_buf[1];
                DebugPrint(string.Format("Enqueue. Bytes read: {0:D}. Timestamp: {1:X2}", ret, raw_buf[1]), DebugType.THREADING);
            }
            return ret;
        }

        private readonly Stopwatch shakeTimer = Stopwatch.StartNew(); //Setup a timer for measuring shake in milliseconds
        private long shakedTime = 0;
        private bool hasShaked;
        bool shakeInputEnabled = Boolean.Parse(ConfigurationManager.AppSettings["EnableShakeInput"]);
        float shakeSensitivity = float.Parse(ConfigurationManager.AppSettings["ShakeInputSensitivity"]);
        float shakeDelay = float.Parse(ConfigurationManager.AppSettings["ShakeInputDelay"]);
        void DetectShake() {
            if (shakeInputEnabled) {
                long currentShakeTime = shakeTimer.ElapsedMilliseconds;

                // Shake detection logic
                bool isShaking = GetAccel().LengthSquared() >= shakeSensitivity;
                if (isShaking && currentShakeTime >= shakedTime + shakeDelay || isShaking && shakedTime == 0) {
                    shakedTime = currentShakeTime;
                    hasShaked = true;

                    // Mapped shake key down
                    Simulate(Config.Value("shake"), false, false);
                    DebugPrint("Shaked at time: " + shakedTime.ToString(), DebugType.SHAKE);
                }

                // If controller was shaked then release mapped key after a small delay to simulate a button press, then reset hasShaked
                if (hasShaked && currentShakeTime >= shakedTime + 10) {
                    // Mapped shake key up
                    Simulate(Config.Value("shake"), false, true);
                    DebugPrint("Shake completed", DebugType.SHAKE);
                    hasShaked = false;
                }

            } else {
                shakeTimer.Stop();
                return;
            }
        }

        bool dragToggle = Boolean.Parse(ConfigurationManager.AppSettings["DragToggle"]);
        Dictionary<int, bool> mouse_toggle_btn = new Dictionary<int, bool>();

        // s can be a "+"-joined combo (see Reassign's combo capture) - here that means "simulate
        // all of these together", e.g. a capture bind of "key_17+key_67" presses Ctrl+C. Any
        // joy_ part is silently skipped - there's no "press another virtual controller button"
        // output, that's what SimulateContinous below is for instead.
        private void Simulate(string s, bool click = true, bool up = false) {
            foreach (string part in s.Split('+')) {
                if (part.StartsWith("key_")) {
                    int key = Int32.Parse(part.Substring(4));
                    if (click) {
                        form.SimulateKeyClick(key);
                    } else {
                        if (up) {
                            form.SimulateKeyRelease(key);
                        } else {
                            form.SimulateKeyHold(key);
                        }
                    }
                } else if (part.StartsWith("mse_")) {
                    int button = Int32.Parse(part.Substring(4));
                    if (click) {
                        form.SimulateButtonClick(button);
                    } else {
                        if (dragToggle) {
                            if (!up) {
                                bool release;
                                mouse_toggle_btn.TryGetValue(button, out release);
                                if (release)
                                    form.SimulateButtonRelease(button);
                                else
                                    form.SimulateButtonHold(button);
                                mouse_toggle_btn[button] = !release;
                            }
                        } else {
                            if (up) {
                                form.SimulateButtonRelease(button);
                            } else {
                                form.SimulateButtonHold(button);
                            }
                        }
                    }
                }
            }
        }

        // For Joystick->Joystick inputs - s can likewise be a "+"-joined combo; every joy_ part
        // gets OR'd in (mse_/key_ parts are Simulate's job above, not this one's).
        private void SimulateContinous(int origin, string s) {
            foreach (string part in s.Split('+')) {
                if (part.StartsWith("joy_")) {
                    int button = Int32.Parse(part.Substring(4));
                    buttons[button] |= buttons[origin];
                }
            }
        }

        bool HomeLongPowerOff = Boolean.Parse(ConfigurationManager.AppSettings["HomeLongPowerOff"]);
        long PowerOffInactivityMins = Int32.Parse(ConfigurationManager.AppSettings["PowerOffInactivity"]);

        bool ChangeOrientationDoubleClick = Boolean.Parse(ConfigurationManager.AppSettings["ChangeOrientationDoubleClick"]);
        long lastDoubleClick = -1;

        string extraGyroFeature = ConfigurationManager.AppSettings["GyroToJoyOrMouse"];
        bool UseFilteredIMU = Boolean.Parse(ConfigurationManager.AppSettings["UseFilteredIMU"]);
        // TEMPORARY, for the figure-eight drift investigation (see CODE_REVIEW.md) - off by
        // default since the logging itself (file I/O every ~150ms while gyro-mouse is active) is
        // its own source of timing interference, exactly the kind of thing this investigation has
        // spent most of its effort chasing out of the real path. Only turn on while deliberately
        // capturing a test.
        bool GyroMouseDebugLogging = Boolean.Parse(ConfigurationManager.AppSettings["GyroMouseDebugLogging"]);
        bool GyroMouseDirectCursor = Boolean.Parse(ConfigurationManager.AppSettings["GyroMouseDirectCursor"]);
        int GyroMouseSensitivityX = Int32.Parse(ConfigurationManager.AppSettings["GyroMouseSensitivityX"]);
        int GyroMouseSensitivityY = Int32.Parse(ConfigurationManager.AppSettings["GyroMouseSensitivityY"]);
        const float GyroMouseDefaultScreenTraversalDegrees = 45.0f;
        float GyroMouseScreenTraversalDegrees = float.Parse(ConfigurationManager.AppSettings["GyroMouseScreenTraversalDegrees"]);
        float GyroMouseTighteningThreshold = float.Parse(ConfigurationManager.AppSettings["GyroMouseTighteningThreshold"]);
        int GyroMouseSmoothingTimeMs = Int32.Parse(ConfigurationManager.AppSettings["GyroMouseSmoothingTimeMs"]);
        float GyroMouseSmoothingThreshold = float.Parse(ConfigurationManager.AppSettings["GyroMouseSmoothingThreshold"]);
        float GyroStickSensitivityX = float.Parse(ConfigurationManager.AppSettings["GyroStickSensitivityX"]);
        float GyroStickSensitivityY = float.Parse(ConfigurationManager.AppSettings["GyroStickSensitivityY"]);
        float GyroStickReduction = float.Parse(ConfigurationManager.AppSettings["GyroStickReduction"]);
        bool GyroHoldToggle = Boolean.Parse(ConfigurationManager.AppSettings["GyroHoldToggle"]);
        bool GyroAnalogSliders = Boolean.Parse(ConfigurationManager.AppSettings["GyroAnalogSliders"]);
        int GyroAnalogSensitivity = Int32.Parse(ConfigurationManager.AppSettings["GyroAnalogSensitivity"]);
        byte[] sliderVal = new byte[] { 0, 0 };

        // A/B/X/Y only ever reflect THIS device's own buttons on a Pro controller. A solo
        // Joycon's 4 primary buttons live at the DPAD_* indices instead (labeled a d-pad on the
        // left one, the same 4 buttons Nintendo prints as A/B/X/Y on the right) - and critically,
        // that's still true when joined: ProcessButtonsAndStick's buttons[A/B/X/Y] cross-
        // reference on a joined pair pulls from the OTHER Joycon's DPAD_* to build one merged
        // Pro-style layout for output, so it does NOT represent this specific physical device's
        // own buttons. Checking DPAD_* here instead, unconditionally for every non-Pro case,
        // is what actually stays correct for whichever single physical Joycon the caller means.
        private bool CalibrationConfirmPressed() {
            if (isPro)
                return buttons_down[(int)Button.A] || buttons_down[(int)Button.B] || buttons_down[(int)Button.X] || buttons_down[(int)Button.Y];
            return buttons_down[(int)Button.DPAD_UP] || buttons_down[(int)Button.DPAD_DOWN] || buttons_down[(int)Button.DPAD_LEFT] || buttons_down[(int)Button.DPAD_RIGHT];
        }

        private void DoThingsWithButtons() {
            // Checked first and returns early like the other button-driven side effects below -
            // a face button doubling as "confirm" only ever matters while a calibration prompt
            // is actually showing (PendingConfirmController names this exact controller only
            // then), so there's no real conflict with its normal mapped behavior the rest of the
            // time.
            if (CalibrationState.PendingConfirmController == this && CalibrationConfirmPressed()) {
                form.HandleCalibrationConfirm(this);
                return;
            }

            int powerOffButton = (int)((isPro || !isLeft || other != null) ? Button.HOME : Button.CAPTURE);

            long timestamp = Stopwatch.GetTimestamp();
            if (HomeLongPowerOff && buttons[powerOffButton]) {
                if ((timestamp - buttons_down_timestamp[powerOffButton]) / 10000 > 2000.0) {
                    if (other != null)
                        other.PowerOff();

                    PowerOff();
                    return;
                }
            }

            if (ChangeOrientationDoubleClick && buttons_down[(int)Button.STICK] && lastDoubleClick != -1 && !isPro) {
                if ((buttons_down_timestamp[(int)Button.STICK] - lastDoubleClick) < 3000000) {
                    form.JoinOrSplitJoycon(this); // trigger connection button click

                    lastDoubleClick = buttons_down_timestamp[(int)Button.STICK];
                    return;
                }
                lastDoubleClick = buttons_down_timestamp[(int)Button.STICK];
            } else if (ChangeOrientationDoubleClick && buttons_down[(int)Button.STICK] && !isPro) {
                lastDoubleClick = buttons_down_timestamp[(int)Button.STICK];
            }

            if (PowerOffInactivityMins > 0) {
                if ((timestamp - inactivity) / 10000 > PowerOffInactivityMins * 60 * 1000) {
                    if (other != null)
                        other.PowerOff();

                    PowerOff();
                    return;
                }
            }

            DetectShake();

            if (buttons_down[(int)Button.CAPTURE])
                Simulate(Config.Value("capture"));
            if (buttons_down[(int)Button.HOME])
                Simulate(Config.Value("home"));
            SimulateContinous((int)Button.CAPTURE, Config.Value("capture"));
            SimulateContinous((int)Button.HOME, Config.Value("home"));

            if (isLeft) {
                if (buttons_down[(int)Button.SL])
                    Simulate(Config.Value("sl_l"), false, false);
                if (buttons_up[(int)Button.SL])
                    Simulate(Config.Value("sl_l"), false, true);
                if (buttons_down[(int)Button.SR])
                    Simulate(Config.Value("sr_l"), false, false);
                if (buttons_up[(int)Button.SR])
                    Simulate(Config.Value("sr_l"), false, true);

                SimulateContinous((int)Button.SL, Config.Value("sl_l"));
                SimulateContinous((int)Button.SR, Config.Value("sr_l"));
            } else {
                if (buttons_down[(int)Button.SL])
                    Simulate(Config.Value("sl_r"), false, false);
                if (buttons_up[(int)Button.SL])
                    Simulate(Config.Value("sl_r"), false, true);
                if (buttons_down[(int)Button.SR])
                    Simulate(Config.Value("sr_r"), false, false);
                if (buttons_up[(int)Button.SR])
                    Simulate(Config.Value("sr_r"), false, true);

                SimulateContinous((int)Button.SL, Config.Value("sl_r"));
                SimulateContinous((int)Button.SR, Config.Value("sr_r"));
            }

            // Filtered IMU data
            this.cur_rotation = AHRS.GetEulerAngles();

            long nowTimestamp = Stopwatch.GetTimestamp();
            float dt = lastDoThingsTimestamp < 0
                ? 0.015f // no prior packet to measure from yet - same assumption this always used
                : (float)((nowTimestamp - lastDoThingsTimestamp) / (double)Stopwatch.Frequency);
            lastDoThingsTimestamp = nowTimestamp;

            // "Re-Centre Gyro" is a one-shot orientation operation, not merely a request to move
            // the Windows pointer. Apply it before sliders/stick/mouse consume this packet so the
            // pose held at the rising edge is neutral immediately. The legacy config key remains
            // reset_mouse for compatibility; only mouse mode also moves the visible cursor.
            string resetMouseVal = Config.Value("reset_mouse");
            if (resetMouseVal != "0") {
                bool resetMouseHeld = IsComboHeld(resetMouseVal);
                if (resetMouseHeld && !prevResetMouseComboHeld) {
                    if (extraGyroFeature == "mouse" &&
                        (isPro || other == null ||
                         (other != null && (Boolean.Parse(ConfigurationManager.AppSettings["GyroMouseLeftHanded"]) ? isLeft : !isLeft)))) {
                        form.SimulateMoveToScreenCenter();
                    }

                    RecenterGyro();
                    dt = 0.0f;
                    LogGyroMouseDiagnosticMarker("RESET");
                }
                prevResetMouseComboHeld = resetMouseHeld;
            }

            if (GyroAnalogSliders && (other != null || isPro)) {
                Button leftT = isLeft ? Button.SHOULDER_2 : Button.SHOULDER2_2;
                Button rightT = isLeft ? Button.SHOULDER2_2 : Button.SHOULDER_2;
                Joycon left = isLeft ? this : (isPro ? this : this.other); Joycon right = !isLeft ? this : (isPro ? this : this.other);

                int ldy, rdy;
                if (UseFilteredIMU) {
                    ldy = (int)(GyroAnalogSensitivity * (left.cur_rotation[0] - left.cur_rotation[3]));
                    rdy = (int)(GyroAnalogSensitivity * (right.cur_rotation[0] - right.cur_rotation[3]));
                } else {
                    ldy = (int)(GyroAnalogSensitivity * (left.gyr_g.Y * dt));
                    rdy = (int)(GyroAnalogSensitivity * (right.gyr_g.Y * dt));
                }

                if (buttons[(int)leftT]) {
                    sliderVal[0] = (byte)Math.Min(Byte.MaxValue, Math.Max(0, (int)sliderVal[0] + ldy));
                } else {
                    sliderVal[0] = 0;
                }

                if (buttons[(int)rightT]) {
                    sliderVal[1] = (byte)Math.Min(Byte.MaxValue, Math.Max(0, (int)sliderVal[1] + rdy));
                } else {
                    sliderVal[1] = 0;
                }
            }

            // active_gyro can be a single bind or a "+"-joined combo mixing controller/keyboard/
            // mouse inputs together (see IsComboHeld) - either way it's evaluated fresh every
            // packet rather than reacting to individual key/button transitions, since a combo
            // needs the simultaneous state of everything in it, not just whichever one last
            // changed. "0" (unbound) trivially holds true for an empty combo, which would
            // otherwise flip active_gyro on every packet - guarded against explicitly.
            string activeGyroCombo = Config.Value("active_gyro");
            if (activeGyroCombo != "0") {
                bool comboHeld = IsComboHeld(activeGyroCombo);
                if (GyroHoldToggle) {
                    active_gyro = comboHeld;
                } else if (comboHeld && !prevActiveGyroComboHeld) {
                    active_gyro = !active_gyro;
                }
                prevActiveGyroComboHeld = comboHeld;
            }

            if (extraGyroFeature.Substring(0, 3) == "joy") {
                if (Config.Value("active_gyro") == "0" || active_gyro) {
                    float[] control_stick = (extraGyroFeature == "joy_left") ? stick : stick2;

                    float dx, dy;
                    if (UseFilteredIMU) {
                        dx = (GyroStickSensitivityX * (cur_rotation[1] - cur_rotation[4])); // yaw
                        dy = -(GyroStickSensitivityY * (cur_rotation[0] - cur_rotation[3])); // pitch
                    } else {
                        dx = (GyroStickSensitivityX * (gyr_g.Z * dt)); // yaw
                        dy = -(GyroStickSensitivityY * (gyr_g.Y * dt)); // pitch
                    }

                    control_stick[0] = Math.Max(-1.0f, Math.Min(1.0f, control_stick[0] / GyroStickReduction + dx));
                    control_stick[1] = Math.Max(-1.0f, Math.Min(1.0f, control_stick[1] / GyroStickReduction + dy));
                }
            } else if (extraGyroFeature == "mouse" && (isPro || (other == null) || (other != null && (Boolean.Parse(ConfigurationManager.AppSettings["GyroMouseLeftHanded"]) ? isLeft : !isLeft)))) {
                // gyro data is in degrees/s
                if (Config.Value("active_gyro") == "0" || active_gyro) {
                    // Mouse movement itself is applied per IMU sub-sample in
                    // ProcessGyroMouseSample. Both modes derive displacement only from angular
                    // velocity. Filtered mode additionally uses a fused gravity reference to
                    // blend yaw/roll in Player Space as the controller is tilted; acceleration
                    // itself never becomes cursor displacement.

                    SimulateGyroMouseButton("left_click", (int)WindowsInput.Events.ButtonCode.Left);
                    SimulateGyroMouseButton("right_click", (int)WindowsInput.Events.ButtonCode.Right);
                    SimulateGyroMouseButton("center_click", (int)WindowsInput.Events.ButtonCode.Middle);
                    SimulateGyroMouseScroll("scroll_up", true);
                    SimulateGyroMouseScroll("scroll_down", false);
                }

            }
        }

        // Gyro-mouse movement is calculated once per IMU sub-sample from ReceiveRaw rather than
        // once per report. gyr_g reflects whichever sub-sample ExtractIMUValues just parsed
        // immediately before this is called; the two must always be called as a pair. Each
        // bundled sub-sample is a fixed ~5ms apart internally (matching MadgwickAHRS's own
        // SamplePeriod and the Timestamp += 5000 bookkeeping already in ReceiveRaw's loop), so
        // this uses that fixed period rather than report-level wall-clock time.
        //
        // Sub-pixel remainder left over from ProcessGyroMouseSample's int truncation, carried
        // into the next sample instead of discarded - three sub-samples a report, each covering
        // only ~5ms, means a slow/deliberate rotation's per-sample delta is very often under 1.0
        // in magnitude on its own. Truncating that straight to 0 every time would zero out slow,
        // precise movement entirely and only respond to fast motion - accumulating the remainder
        // means that same slow rotation still adds up to real movement, just spread over a few
        // more samples rather than lost.
        private float pendingMouseDx, pendingMouseDy;

        // Canonical JoyShockLibrary/GamepadMotionHelpers Y-up sensor frame used only by gyro
        // mouse. BetterJoy's public gyr_g/acc_g frame is retained untouched for UDP, gyro-stick,
        // analog sliders and compatibility. A solo Joy-Con gets an additional proper sideways
        // rotation, applied identically to these two vectors before fusion.
        private Vector3 gyroMouseSensorRate;
        private Vector3 gyroMouseSensorAccel;

        // Constant roll correction captured by Re-Centre Gyro. Rows describe the neutral X/Y
        // axes in the canonical sensor frame; Z is the controller's pointing/roll axis and is
        // unchanged. Identity preserves the normal Pro/paired/sideways defaults until recentered.
        private Vector2 gyroMouseNeutralX = new Vector2(1.0f, 0.0f);
        private Vector2 gyroMouseNeutralY = new Vector2(0.0f, 1.0f);

        // Smoothing removes high-frequency noise but deliberately preserves DC, including the
        // small temperature/unit-specific zero-rate bias that shows up as a steady cursor crawl
        // while a Joycon is sitting untouched. Learn that bias only from a sustained stillness
        // window. Accelerometer magnitude is used strictly as a confidence gate (near 1g means
        // no obvious linear acceleration); accelerometer direction never enters cursor motion.
        private const int GyroMouseBiasWindowSamples = 100; // 0.5s at 200 Hz
        private const float GyroMouseInitialStillRateLimit = 2.0f; // degrees/sec per axis
        private const float GyroMouseLearnedStillRateLimit = 1.25f;
        private const float GyroMouseStillRangeLimit = 1.0f;
        private const float GyroMouseStillAccelTolerance = 0.15f;
        private Vector3 gyroMouseBias;
        private bool gyroMouseBiasInitialized;
        private Vector3 gyroMouseBiasWindowSum;
        private Vector3 gyroMouseBiasWindowMin;
        private Vector3 gyroMouseBiasWindowMax;
        private int gyroMouseBiasWindowCount;

        // Raw mode retains the gyro-only quaternion mapper for A/B comparison. Filtered mode uses
        // the proven Player Space approach from GamepadMotionHelpers: gravity influences which
        // gyro axis means horizontal, but only gyro rate can produce movement.
        private readonly GyroMouseOrientation gyroMouseOrientation = new GyroMouseOrientation();
        private readonly GyroMousePlayerSpace gyroMousePlayerSpace = new GyroMousePlayerSpace();

        // Smooth mapped 2D motion, not the raw 3D sensor. The filtered state is blended back
        // toward the live rate as speed rises, preserving fine-motion stability without making
        // fast turns feel delayed.
        private Vector2 filteredGyroMouseRate;
        private bool filteredGyroMouseRateInitialized;

        // A solo Joycon is held sideways and ExtractIMUValues rotates its gyro axes; a joined
        // Joycon uses the pair/vertical basis instead. Keeping an orientation integrated in the
        // old basis after other changes would mix two coordinate systems and make gyro-mouse or
        // another filtered gyro feature jump/bend badly after join/split. This snapshot is read
        // and updated only by the controller's poll thread.
        private Joycon gyroMouseOrientationPartner;

        // TEMPORARY diagnostic instrumentation for the figure-eight/circle drift investigation
        // (see CODE_REVIEW.md). Everything below is scoped to the CURRENT interval only (reset
        // after every write) rather than a lifetime running average - a lifetime average is too
        // smoothed out to see what's actually happening moment to moment. Interval length matches
        // the report/write cadence: short enough (150ms) to see shape within a single loop, long
        // enough that the file stays readable. Remove once the investigation concludes.
        //
        // Review-flagged fix: File.AppendAllText used to be called directly from here, i.e. on a
        // Joycon's own poll thread, roughly every 150ms - synchronous file I/O on the exact path
        // whose timing this investigation cares about, capable of distorting the jank/burstiness
        // being measured. Formatted lines are now only ever enqueued (cheap, no I/O) here; a
        // dedicated background thread (DiagLogWriterLoop) drains the queue and does the actual
        // write, off this path entirely.
        private const double DiagLogIntervalSeconds = 0.15;
        private static readonly ConcurrentQueue<string> diagLogQueue = new ConcurrentQueue<string>();
        private static int diagLogWriterStarted;

        private long diagIntervalDx, diagIntervalDy, diagIntervalSampleCount;
        private long diagIntervalPositiveCount, diagIntervalNegativeCount;
        private double diagIntervalSumGyrGY, diagIntervalSumRawGyrY;
        private float diagIntervalMinGyrGY = float.MaxValue, diagIntervalMaxGyrGY = float.MinValue;
        private long diagLastLogTimestamp;

        // Raw yaw/roll rates (gyr_g.Z/X - gyr_g.Y=pitch is tracked above), the quaternion-derived
        // orientation roll, and the orientation-mapped yaw/pitch rates that actually reach
        // sensitivity scaling - side by side so mapping behavior is directly visible.
        private double diagIntervalSumGyrGZ, diagIntervalSumGyrGX, diagIntervalSumRollDeg;
        private double diagIntervalSumYawRate, diagIntervalSumPitchRate;
        private float diagIntervalMinGyrGZ = float.MaxValue, diagIntervalMaxGyrGZ = float.MinValue;
        private float diagIntervalMinGyrGX = float.MaxValue, diagIntervalMaxGyrGX = float.MinValue;
        private float diagIntervalMinRollDeg = float.MaxValue, diagIntervalMaxRollDeg = float.MinValue;

        // Auto-detected "controller genuinely at rest" periods, marked in the log so a stationary
        // window doesn't have to be manually timestamped and reported separately - can't just
        // threshold gyr_g.Y's raw magnitude (a biased reading won't sit near zero even at true
        // rest, that's the whole bug), so this tracks how much gyr_g.Y VARIES over a running
        // streak instead: a genuinely still controller holds a narrow band (whatever its bias
        // happens to be), real wrist motion breaks out of a narrow band almost immediately.
        private const float StillnessSpreadThresholdDegPerSec = 3.0f;
        private const double StillnessMinDurationSeconds = 10.0;
        private float stillStreakMinGyrGY = float.MaxValue, stillStreakMaxGyrGY = float.MinValue;
        private long stillStreakStartTimestamp;
        private bool stillStreakMarked;

        // Started lazily on first use rather than from a constructor - matches how the rest of
        // this diagnostic code only activates once GyroMouseDebugLogging/actual gyro-mouse use
        // requires it, instead of running for every Joycon regardless of whether it's ever used.
        private static void EnsureDiagLogWriterStarted() {
            if (Interlocked.CompareExchange(ref diagLogWriterStarted, 1, 0) != 0)
                return;
            new Thread(DiagLogWriterLoop) { IsBackground = true, Name = "GyroMouseDiagLogWriter" }.Start();
        }

        private static void DiagLogWriterLoop() {
            string logPath = Path.Combine(AppPaths.DataDir, "gyro_mouse_debug.log");
            while (true) {
                Thread.Sleep(500);
                if (diagLogQueue.IsEmpty)
                    continue;

                var batch = new StringBuilder();
                while (diagLogQueue.TryDequeue(out string line))
                    batch.Append(line);

                try {
                    File.AppendAllText(logPath, batch.ToString());
                } catch {
                    // diagnostic only - never let logging itself take down gyro-mouse
                }
            }
        }

        // Called for every sub-sample - accumulates this interval's stats and runs the stillness
        // streak check - and again whenever a flush actually injects movement (with the real
        // dx/dy that were sent, 0/0 otherwise). Enqueues at most once per DiagLogIntervalSeconds.
        private void RecordGyroMouseDiagnosticSample(int dx, int dy, float rollDeg, float yawRate, float pitchRate) {
            if (!GyroMouseDebugLogging)
                return;

            EnsureDiagLogWriterStarted();

            diagIntervalDx += dx;
            diagIntervalDy += dy;
            diagIntervalSumGyrGY += gyr_g.Y;
            diagIntervalSumRawGyrY += gyr_r[1];
            diagIntervalSampleCount++;
            if (gyr_g.Y >= 0) diagIntervalPositiveCount++; else diagIntervalNegativeCount++;
            if (gyr_g.Y < diagIntervalMinGyrGY) diagIntervalMinGyrGY = gyr_g.Y;
            if (gyr_g.Y > diagIntervalMaxGyrGY) diagIntervalMaxGyrGY = gyr_g.Y;

            diagIntervalSumGyrGZ += gyr_g.Z;
            if (gyr_g.Z < diagIntervalMinGyrGZ) diagIntervalMinGyrGZ = gyr_g.Z;
            if (gyr_g.Z > diagIntervalMaxGyrGZ) diagIntervalMaxGyrGZ = gyr_g.Z;

            diagIntervalSumGyrGX += gyr_g.X;
            if (gyr_g.X < diagIntervalMinGyrGX) diagIntervalMinGyrGX = gyr_g.X;
            if (gyr_g.X > diagIntervalMaxGyrGX) diagIntervalMaxGyrGX = gyr_g.X;

            diagIntervalSumRollDeg += rollDeg;
            if (rollDeg < diagIntervalMinRollDeg) diagIntervalMinRollDeg = rollDeg;
            if (rollDeg > diagIntervalMaxRollDeg) diagIntervalMaxRollDeg = rollDeg;

            diagIntervalSumYawRate += yawRate;
            diagIntervalSumPitchRate += pitchRate;

            UpdateStillnessStreak();

            long now = Stopwatch.GetTimestamp();
            if (diagLastLogTimestamp != 0 && (now - diagLastLogTimestamp) / (double)Stopwatch.Frequency < DiagLogIntervalSeconds)
                return;
            diagLastLogTimestamp = now;

            bool allowCalibration = Boolean.Parse(ConfigurationManager.AppSettings["AllowCalibration"]);
            float neutralValue = allowCalibration ? activeData[1] : gyr_neutral[1];

            string line = string.Format(
                "{0:HH:mm:ss.fff}  Y(pitch,raw): avg={1,7:F3} min={2,7:F3} max={3,7:F3} pos={4,4} neg={5,4}  |  Z(yaw,raw): avg={6,7:F3} min={7,7:F3} max={8,7:F3}  |  X(roll rate): avg={9,7:F3} min={10,7:F3} max={11,7:F3}  |  Roll angle(quat): avg={12,7:F2} min={13,7:F2} max={14,7:F2}deg  |  mapped: yaw avg={15,7:F3} pitch avg={16,7:F3}  |  raw gyr_r[1] avg={17,8:F1} neutral({18})={19,8:F1}  |  interval dx={20,5} dy={21,5}  samples={22,4}\r\n",
                DateTime.Now,
                diagIntervalSumGyrGY / diagIntervalSampleCount, diagIntervalMinGyrGY, diagIntervalMaxGyrGY,
                diagIntervalPositiveCount, diagIntervalNegativeCount,
                diagIntervalSumGyrGZ / diagIntervalSampleCount, diagIntervalMinGyrGZ, diagIntervalMaxGyrGZ,
                diagIntervalSumGyrGX / diagIntervalSampleCount, diagIntervalMinGyrGX, diagIntervalMaxGyrGX,
                diagIntervalSumRollDeg / diagIntervalSampleCount, diagIntervalMinRollDeg, diagIntervalMaxRollDeg,
                diagIntervalSumYawRate / diagIntervalSampleCount, diagIntervalSumPitchRate / diagIntervalSampleCount,
                diagIntervalSumRawGyrY / diagIntervalSampleCount,
                allowCalibration ? "activeData[1]" : "gyr_neutral[1]", neutralValue,
                diagIntervalDx, diagIntervalDy, diagIntervalSampleCount);
            diagLogQueue.Enqueue(line);

            diagIntervalDx = 0; diagIntervalDy = 0; diagIntervalSampleCount = 0;
            diagIntervalPositiveCount = 0; diagIntervalNegativeCount = 0;
            diagIntervalSumGyrGY = 0; diagIntervalSumRawGyrY = 0;
            diagIntervalMinGyrGY = float.MaxValue; diagIntervalMaxGyrGY = float.MinValue;
            diagIntervalSumGyrGZ = 0; diagIntervalSumGyrGX = 0; diagIntervalSumRollDeg = 0;
            diagIntervalSumYawRate = 0; diagIntervalSumPitchRate = 0;
            diagIntervalMinGyrGZ = float.MaxValue; diagIntervalMaxGyrGZ = float.MinValue;
            diagIntervalMinGyrGX = float.MaxValue; diagIntervalMaxGyrGX = float.MinValue;
            diagIntervalMinRollDeg = float.MaxValue; diagIntervalMaxRollDeg = float.MinValue;
        }

        private void UpdateStillnessStreak() {
            float candidateMin = Math.Min(stillStreakMinGyrGY, gyr_g.Y);
            float candidateMax = Math.Max(stillStreakMaxGyrGY, gyr_g.Y);

            if (candidateMax - candidateMin > StillnessSpreadThresholdDegPerSec) {
                if (stillStreakMarked)
                    LogGyroMouseDiagnosticMarker("STATIONARY END");

                stillStreakMinGyrGY = gyr_g.Y;
                stillStreakMaxGyrGY = gyr_g.Y;
                stillStreakStartTimestamp = Stopwatch.GetTimestamp();
                stillStreakMarked = false;
                return;
            }

            stillStreakMinGyrGY = candidateMin;
            stillStreakMaxGyrGY = candidateMax;

            if (!stillStreakMarked) {
                double elapsed = (Stopwatch.GetTimestamp() - stillStreakStartTimestamp) / (double)Stopwatch.Frequency;
                if (elapsed >= StillnessMinDurationSeconds) {
                    LogGyroMouseDiagnosticMarker(string.Format("STATIONARY START (held within {0}deg/s band for {1:F1}s so far)", StillnessSpreadThresholdDegPerSec, elapsed));
                    stillStreakMarked = true;
                }
            }
        }

        // Marks a single instant in the log - reset_mouse actually firing, or an auto-detected
        // stillness streak starting/ending - so the regular interval lines above can be lined up
        // against it. TEMPORARY, same as RecordGyroMouseDiagnosticSample.
        private void LogGyroMouseDiagnosticMarker(string label) {
            if (!GyroMouseDebugLogging)
                return;

            EnsureDiagLogWriterStarted();
            diagLogQueue.Enqueue(string.Format("{0:HH:mm:ss.fff}  *** {1} ***\r\n", DateTime.Now, label));
        }

        private void ResetGyroMouseMotionState(bool resetPlayerSpace = false) {
            pendingMouseDx = pendingMouseDy = 0.0f;
            gyroMouseOrientation.Reset();
            if (resetPlayerSpace)
                gyroMousePlayerSpace.Reset();
            filteredGyroMouseRate = Vector2.Zero;
            filteredGyroMouseRateInitialized = false;
        }

        private void ResetGyroMouseBiasWindow() {
            gyroMouseBiasWindowSum = Vector3.Zero;
            gyroMouseBiasWindowMin = Vector3.Zero;
            gyroMouseBiasWindowMax = Vector3.Zero;
            gyroMouseBiasWindowCount = 0;
        }

        private void ResetGyroMouseBiasEstimator() {
            gyroMouseBias = Vector3.Zero;
            gyroMouseBiasInitialized = false;
            ResetGyroMouseBiasWindow();
        }

        private static float MaxAbsComponent(Vector3 value) {
            return Math.Max(Math.Abs(value.X), Math.Max(Math.Abs(value.Y), Math.Abs(value.Z)));
        }

        // Returns gyro rate with the learned stationary zero-rate offset removed. Before the
        // first stable 0.5s window completes, a sample that is itself a stillness candidate is
        // suppressed rather than allowed to crawl the cursor; deliberate motion immediately
        // breaks the window and passes through normally.
        private Vector3 ApplyGyroMouseStationaryBias(Vector3 rawRate, bool allowBiasLearning) {
            Vector3 residual = rawRate - gyroMouseBias;

            // A constant slow yaw is indistinguishable from bias using only gyro and gravity:
            // rotation around gravity does not change the accelerometer direction. Never let an
            // already-calibrated pointer learn while active, or deliberate slow movement in one
            // direction becomes the new zero and makes that direction feel like it is fighting
            // the user. Initial lock is still allowed so always-on gyro can remove startup drift;
            // subsequent temperature adjustment happens while the activation bind is released.
            if (gyroMouseBiasInitialized && !allowBiasLearning) {
                ResetGyroMouseBiasWindow();
                return residual;
            }

            float stillRateLimit = gyroMouseBiasInitialized
                ? GyroMouseLearnedStillRateLimit
                : GyroMouseInitialStillRateLimit;
            float accelMagnitude = gyroMouseSensorAccel.Length();
            bool stillCandidate = Math.Abs(accelMagnitude - 1.0f) <= GyroMouseStillAccelTolerance &&
                                  MaxAbsComponent(residual) <= stillRateLimit;

            if (!stillCandidate) {
                ResetGyroMouseBiasWindow();
                return residual;
            }

            if (gyroMouseBiasWindowCount == 0) {
                gyroMouseBiasWindowMin = rawRate;
                gyroMouseBiasWindowMax = rawRate;
            } else {
                gyroMouseBiasWindowMin = new Vector3(
                    Math.Min(gyroMouseBiasWindowMin.X, rawRate.X),
                    Math.Min(gyroMouseBiasWindowMin.Y, rawRate.Y),
                    Math.Min(gyroMouseBiasWindowMin.Z, rawRate.Z));
                gyroMouseBiasWindowMax = new Vector3(
                    Math.Max(gyroMouseBiasWindowMax.X, rawRate.X),
                    Math.Max(gyroMouseBiasWindowMax.Y, rawRate.Y),
                    Math.Max(gyroMouseBiasWindowMax.Z, rawRate.Z));
            }

            gyroMouseBiasWindowSum += rawRate;
            gyroMouseBiasWindowCount++;

            if (gyroMouseBiasWindowCount >= GyroMouseBiasWindowSamples) {
                Vector3 range = gyroMouseBiasWindowMax - gyroMouseBiasWindowMin;
                if (MaxAbsComponent(range) <= GyroMouseStillRangeLimit) {
                    Vector3 measuredBias = gyroMouseBiasWindowSum / gyroMouseBiasWindowCount;
                    bool firstBiasLock = !gyroMouseBiasInitialized;
                    gyroMouseBias = gyroMouseBiasInitialized
                        ? gyroMouseBias + 0.2f * (measuredBias - gyroMouseBias)
                        : measuredBias;
                    gyroMouseBiasInitialized = true;
                    residual = rawRate - gyroMouseBias;
                    if (firstBiasLock) {
                        LogGyroMouseDiagnosticMarker(string.Format(
                            "GYRO BIAS LOCK x={0:F3} y={1:F3} z={2:F3} deg/s",
                            gyroMouseBias.X, gyroMouseBias.Y, gyroMouseBias.Z));
                    }
                }
                ResetGyroMouseBiasWindow();
            }

            return gyroMouseBiasInitialized ? residual : Vector3.Zero;
        }

        // Makes the pose held at the moment the Re-Centre Gyro bind is pressed the new neutral
        // orientation. This intentionally does not touch activeData/gyr_neutral/acc calibration:
        // recentering is a coordinate-frame change, while calibration estimates sensor offsets.
        private void RecenterGyro() {
            CaptureGyroMouseNeutralFrame();

            // Throw away the old gravity frame as well as pending mouse motion. The next IMU
            // sub-sample seeds gravity in the newly captured grip frame. Bias is intentionally
            // retained in the underlying sensor frame: orientation and calibration are separate.
            ResetGyroMouseMotionState(true);
            ResetGyroMouseBiasWindow();
            AHRS.Recenter();
            cur_rotation = AHRS.GetEulerAngles();
            lastDoThingsTimestamp = -1;
        }

        // A solo Joycon and a joined Joycon transform the same physical IMU into different
        // coordinate bases in ExtractIMUValues. Never carry either orientation estimator across
        // that boundary. Kept on the Poll thread so it cannot race AHRS.Update/MapSample.
        private void EnsureGyroOrientationBasis() {
            Joycon currentPartner = other;
            if (Object.ReferenceEquals(currentPartner, gyroMouseOrientationPartner))
                return;

            gyroMouseNeutralX = new Vector2(1.0f, 0.0f);
            gyroMouseNeutralY = new Vector2(0.0f, 1.0f);
            ResetGyroMouseMotionState(true);
            ResetGyroMouseBiasEstimator();
            AHRS.Reset();
            gyroMouseOrientationPartner = currentPartner;
        }

        private void MoveGyroMouseBy(int dx, int dy) {
            if (GyroMouseDirectCursor)
                form.SimulateCursorMoveBy(dx, dy);
            else
                form.SimulateMoveBy(dx, dy);
        }

        private void UpdateCanonicalGyroMouseImu() {
            // BetterJoy parses Nintendo packet axes as X=raw Z, Y=raw X, Z=raw Y and applies
            // controller-side signs. This proper rotation converts that established frame to the
            // same Y-up convention JoyShockLibrary feeds into GamepadMotionHelpers.
            gyroMouseSensorAccel = new Vector3(-acc_g.Y, acc_g.Z, -acc_g.X);
            gyroMouseSensorRate = new Vector3(gyr_g.Y, -gyr_g.Z, -gyr_g.X);

            if (other == null && !isPro) {
                float oldAccelX = gyroMouseSensorAccel.X;
                float oldAccelZ = gyroMouseSensorAccel.Z;
                float oldGyroX = gyroMouseSensorRate.X;
                float oldGyroZ = gyroMouseSensorRate.Z;

                if (isLeft) {
                    // +90 degrees around canonical up: (x,y,z) -> (z,y,-x).
                    gyroMouseSensorAccel.X = oldAccelZ;
                    gyroMouseSensorAccel.Z = -oldAccelX;
                    gyroMouseSensorRate.X = oldGyroZ;
                    gyroMouseSensorRate.Z = -oldGyroX;
                } else {
                    // -90 degrees around canonical up: (x,y,z) -> (-z,y,x).
                    gyroMouseSensorAccel.X = -oldAccelZ;
                    gyroMouseSensorAccel.Z = oldAccelX;
                    gyroMouseSensorRate.X = -oldGyroZ;
                    gyroMouseSensorRate.Z = oldGyroX;
                }
            }
        }

        private Vector3 TransformGyroMouseToNeutralFrame(Vector3 value) {
            return new Vector3(
                gyroMouseNeutralX.X * value.X + gyroMouseNeutralX.Y * value.Y,
                gyroMouseNeutralY.X * value.X + gyroMouseNeutralY.Y * value.Y,
                value.Z);
        }

        private void CaptureGyroMouseNeutralFrame() {
            float accelLength = gyroMouseSensorAccel.Length();
            if (accelLength <= 0.0f)
                return;

            Vector3 down = -gyroMouseSensorAccel / accelLength;
            float projectedLength = (float)Math.Sqrt(down.X * down.X + down.Y * down.Y);
            if (projectedLength <= 0.1f)
                return; // pointing almost vertically: grip roll is undefined

            float inverseLength = 1.0f / projectedLength;
            float downX = down.X * inverseLength;
            float downY = down.Y * inverseLength;

            // Express future samples in axes where the current projected gravity is (0,-1).
            // That makes the user's present grip define local pitch/up-down without altering the
            // forward Z axis or allowing acceleration itself to create mouse displacement.
            gyroMouseNeutralX = new Vector2(-downY, downX);
            gyroMouseNeutralY = new Vector2(-downX, -downY);
        }

        private void SmoothGyroMouseRates(ref float yawRate, ref float pitchRate,
                                          float samplePeriod) {
            Vector2 current = new Vector2(yawRate, pitchRate);
            if (GyroMouseSmoothingTimeMs <= 0 || GyroMouseSmoothingThreshold <= 0.0f) {
                filteredGyroMouseRate = current;
                filteredGyroMouseRateInitialized = true;
                return;
            }

            if (!filteredGyroMouseRateInitialized) {
                filteredGyroMouseRate = current;
                filteredGyroMouseRateInitialized = true;
            } else {
                float timeConstant = GyroMouseSmoothingTimeMs / 1000.0f;
                float alpha = 1.0f - (float)Math.Exp(-samplePeriod / timeConstant);
                filteredGyroMouseRate += alpha * (current - filteredGyroMouseRate);
            }

            float speed = current.Length();
            float lowerThreshold = GyroMouseSmoothingThreshold * 0.5f;
            float unsmoothedFactor = GyroMouseSmoothingThreshold <= lowerThreshold
                ? 1.0f
                : Math.Max(0.0f, Math.Min(1.0f,
                    (speed - lowerThreshold) /
                    (GyroMouseSmoothingThreshold - lowerThreshold)));

            // Smoothstep avoids a perceptible gain corner as the filter releases. Once fully
            // released, follow the live rate so old slow-motion history cannot create a tail
            // when the user stops after a quick sweep.
            unsmoothedFactor = unsmoothedFactor * unsmoothedFactor *
                               (3.0f - 2.0f * unsmoothedFactor);
            Vector2 result = Vector2.Lerp(filteredGyroMouseRate, current, unsmoothedFactor);
            if (unsmoothedFactor >= 1.0f)
                filteredGyroMouseRate = current;
            yawRate = result.X;
            pitchRate = result.Y;
        }

        // flushToMouse: integrate every sub-sample (all 3, for accuracy - see the field comment
        // above), but only actually call SimulateMoveBy once per report (the last sub-sample),
        // matching the pre-fix call rate. Calling it 3x/report instead tripled the pipe-write
        // rate in service mode (HeadlessJoyconHost.SendMessage's queue, see the fix there) - under
        // sustained motion that can outrun the writer thread and start dropping the newest
        // messages, which reads as the same "constrained" symptom this was meant to fix, just
        // from a different cause, and would plausibly cycle with motion intensity (queue fills
        // during a burst, drains during a lull) rather than being constant.
        private void ProcessGyroMouseSample(bool flushToMouse) {
            EnsureGyroOrientationBasis();

            if (extraGyroFeature != "mouse") {
                ResetGyroMouseMotionState(true);
                return;
            }
            if (!(isPro || (other == null) || (other != null && (Boolean.Parse(ConfigurationManager.AppSettings["GyroMouseLeftHanded"]) ? isLeft : !isLeft)))) {
                ResetGyroMouseMotionState(true);
                return;
            }

            // Keep learning the selected controller's zero-rate bias while gyro-mouse is
            // inactive, so activating it after the controller has been resting does not begin
            // with half a second of cursor crawl.
            bool gyroPointerActive = Config.Value("active_gyro") == "0" || active_gyro;
            Vector3 mouseGyroRate = gyr_g;
            Vector3 mouseAccel = acc_g;
            if (UseFilteredIMU) {
                Vector3 calibratedSensorRate = ApplyGyroMouseStationaryBias(
                    gyroMouseSensorRate, !gyroPointerActive);
                mouseGyroRate = TransformGyroMouseToNeutralFrame(calibratedSensorRate);
                mouseAccel = TransformGyroMouseToNeutralFrame(gyroMouseSensorAccel);
            }

            const float subSamplePeriod = 0.005f;
            const float degToRad = 0.0174533f;

            // The legacy X/Y sensitivities define the established 45-degree reference gain.
            // Expose the physical range as one intuitive control while preserving that tuned
            // horizontal/vertical balance. Invalid non-positive values safely retain the
            // established default rather than producing an inverted or infinite cursor gain.
            float traversalDegrees = GyroMouseScreenTraversalDegrees;
            if (traversalDegrees <= 0.0f || float.IsNaN(traversalDegrees) ||
                float.IsInfinity(traversalDegrees))
                traversalDegrees = GyroMouseDefaultScreenTraversalDegrees;
            float traversalScale = GyroMouseDefaultScreenTraversalDegrees / traversalDegrees;
            float mouseSensitivityX = GyroMouseSensitivityX * traversalScale;
            float mouseSensitivityY = GyroMouseSensitivityY * traversalScale;

            // Keep the tilt reference current even while the activation button is released.
            // World Space fuses acceleration into the coordinate basis only; this call cannot
            // add cursor displacement.
            if (UseFilteredIMU)
                gyroMousePlayerSpace.Update(mouseGyroRate, mouseAccel, subSamplePeriod);

            if (!gyroPointerActive) {
                pendingMouseDx = pendingMouseDy = 0.0f;
                filteredGyroMouseRate = Vector2.Zero;
                filteredGyroMouseRateInitialized = false;
                return;
            }

            float yawRate = UseFilteredIMU ? mouseGyroRate.Y : mouseGyroRate.Z;
            float pitchRate = UseFilteredIMU ? mouseGyroRate.X : mouseGyroRate.Y;
            float rollRad = 0.0f;

            if (Boolean.Parse(ConfigurationManager.AppSettings["GyroMouseRollCompensation"])) {
                float deltaYawRad;
                float deltaPitchRad;
                if (UseFilteredIMU) {
                    gyroMousePlayerSpace.Map(mouseGyroRate, out yawRate, out pitchRate,
                                             out rollRad);

                    SmoothGyroMouseRates(ref yawRate, ref pitchRate, subSamplePeriod);

                    // JoyShockLibrary's reference mouse sample calls this "tightening": below a
                    // small real-world angular speed, smoothly reduce gain instead of imposing a
                    // deadzone or adding more low-pass lag. At and above the threshold the Player
                    // Space result is unchanged.
                    float inputSpeed = mouseGyroRate.Length();
                    if (GyroMouseTighteningThreshold > 0.0f &&
                        inputSpeed < GyroMouseTighteningThreshold) {
                        float tightening = inputSpeed / GyroMouseTighteningThreshold;
                        yawRate *= tightening;
                        pitchRate *= tightening;
                    }
                    deltaYawRad = yawRate * subSamplePeriod * degToRad;
                    deltaPitchRad = pitchRate * subSamplePeriod * degToRad;
                } else {
                    filteredGyroMouseRate = Vector2.Zero;
                    filteredGyroMouseRateInitialized = false;
                    gyroMouseOrientation.MapSample(
                        mouseGyroRate.X, mouseGyroRate.Y, mouseGyroRate.Z, subSamplePeriod,
                        out deltaYawRad, out deltaPitchRad, out rollRad);

                    // Keep diagnostics comparable to the direct-rate and Player Space paths.
                    yawRate = deltaYawRad / (subSamplePeriod * degToRad);
                    pitchRate = deltaPitchRad / (subSamplePeriod * degToRad);
                }
                pendingMouseDx += mouseSensitivityX * deltaYawRad;
                pendingMouseDy += -(mouseSensitivityY * deltaPitchRad);
            } else {
                gyroMouseOrientation.Reset();
                filteredGyroMouseRate = Vector2.Zero;
                filteredGyroMouseRateInitialized = false;
                pendingMouseDx += mouseSensitivityX * (yawRate * subSamplePeriod * degToRad);
                pendingMouseDy += -(mouseSensitivityY * (pitchRate * subSamplePeriod * degToRad));
            }

            float rollDeg = rollRad * (180.0f / (float)Math.PI);

            if (!flushToMouse) {
                RecordGyroMouseDiagnosticSample(0, 0, rollDeg, yawRate, pitchRate);
                return;
            }

            int dx = (int)pendingMouseDx;
            int dy = (int)pendingMouseDy;
            pendingMouseDx -= dx;
            pendingMouseDy -= dy;

            RecordGyroMouseDiagnosticSample(dx, dy, rollDeg, yawRate, pitchRate);

            if (dx != 0 || dy != 0)
                MoveGyroMouseBy(dx, dy);
        }

        // left_click/right_click/center_click/scroll_up/scroll_down (see App.config's comment on
        // them) - bindable controller buttons that simulate a mouse action, reachable only from
        // inside the same "gyro-mouse is actually active" block as the cursor movement above, so
        // they're inert the rest of the time rather than stealing a button from its normal game
        // mapping. Read fresh from AppSettings each call, not cached in a field the way most
        // other settings here are - so a newly-bound key takes effect immediately instead of
        // needing the controller to reconnect first. Can be a combo like every other bind now
        // (see Reassign.cs), which IsComboHeld handles - this used to be a bare
        // Int32.Parse(val.Substring(4)) on the whole value, which crashed the poll thread with a
        // FormatException the moment val held a "+"-joined combo instead of one plain joy_N.
        private readonly Dictionary<string, bool> gyroMouseComboHeld = new Dictionary<string, bool>();

        private void SimulateGyroMouseButton(string configKey, int buttonCode) {
            string val = ConfigurationManager.AppSettings[configKey] ?? "0";
            if (val == "0")
                return;

            bool held = IsComboHeld(val);
            bool wasHeld = gyroMouseComboHeld.TryGetValue(configKey, out bool prev) && prev;
            gyroMouseComboHeld[configKey] = held;

            if (held && !wasHeld)
                form.SimulateButtonHold(buttonCode);
            else if (!held && wasHeld)
                form.SimulateButtonRelease(buttonCode);
        }

        // Scroll has no hold/release equivalent - just a discrete tick per press, matching a
        // physical scroll wheel's own click detents rather than a continuous rate while held.
        private void SimulateGyroMouseScroll(string configKey, bool up) {
            string val = ConfigurationManager.AppSettings[configKey] ?? "0";
            if (val == "0")
                return;

            bool held = IsComboHeld(val);
            bool wasHeld = gyroMouseComboHeld.TryGetValue(configKey, out bool prev) && prev;
            gyroMouseComboHeld[configKey] = held;

            if (held && !wasHeld)
                form.SimulateScroll(up);
        }

        // Guards RetireDuplicateConnections() above so it only ever runs once per controller,
        // the first time it actually proves itself alive (not merely that Attach() didn't
        // throw, which happens before the connection is known to be stable/receiving real data).
        private bool retiredDuplicates = false;

        private Thread PollThreadObj;

        // Requested LED player-number update, applied by this Joycon's own Poll() thread rather
        // than the caller's - SetLEDByPlayerNum/Subcommand does a blocking HID write+read on the
        // same handle Poll() is concurrently reading from, so calling it directly from a foreign
        // thread (the scan thread doing a mass re-rank after a drop, or Joycon.other's setter
        // during a join/split) on an already-Begin()'d controller risked the response getting
        // interleaved with normal packet reads and the LED update silently timing out - matching
        // the existing rumble_obj queue pattern below, just for a single latest-wins value
        // instead of a FIFO, since only the most recent requested LED value matters. -1 means "no
        // update pending" - Interlocked.Exchange (not volatile, which int? can't be) makes the
        // read-and-clear in Poll() atomic against a concurrent RequestLEDUpdate call.
        private int pendingLedPlayerNum = -1;

        public void RequestLEDUpdate(int playerNum) {
            Interlocked.Exchange(ref pendingLedPlayerNum, playerNum);
        }

        private void Poll() {
            stop_polling = false;
            int attempts = 0;
            while (!stop_polling & state > state_.NO_JOYCONS) {
                int requestedLed = Interlocked.Exchange(ref pendingLedPlayerNum, -1);
                if (requestedLed >= 0) {
                    SetLEDByPlayerNum(requestedLed);
                }
                if (rumble_obj.queue.Count > 0) {
                    SendRumble(rumble_obj.GetData());
                }
                int a = ReceiveRaw();

                if (a > 0 && state > state_.DROPPED) {
                    state = state_.IMU_DATA_OK;
                    attempts = 0;

                    if (!retiredDuplicates) {
                        retiredDuplicates = true;
                        RetireDuplicateConnections();
                    }
                } else if (attempts > 240) {
                    state = state_.DROPPED;
                    form.AppendTextBox("Dropped.\r\n");

                    DebugPrint("Connection lost. Is the Joy-Con connected?", DebugType.ALL);
                    break;
                } else if (a < 0) {
                    // An error on read.
                    //form.AppendTextBox("Pause 5ms");
                    Thread.Sleep((Int32)5);
                    ++attempts;
                } else if (a == 0) {
                    // The non-blocking read timed out. No need to sleep.
                    // No need to increase attempts because it's not an error.
                }
            }
        }

        public float[] otherStick = { 0, 0 };

        bool swapAB = Boolean.Parse(ConfigurationManager.AppSettings["SwapAB"]);
        bool swapXY = Boolean.Parse(ConfigurationManager.AppSettings["SwapXY"]);
        bool realn64Range = Boolean.Parse(ConfigurationManager.AppSettings["N64Range"]);
        float stickScalingFactor = float.Parse(ConfigurationManager.AppSettings["StickScalingFactor"]);
        float stickScalingFactor2 = float.Parse(ConfigurationManager.AppSettings["StickScalingFactor2"]);

        private int ProcessButtonsAndStick(byte[] report_buf) {
            if (report_buf[0] == 0x00) throw new ArgumentException("received undefined report. This is probably a bug");
            if (!isSnes) {
                stick_raw[0] = report_buf[6 + (isLeft ? 0 : 3)];
                stick_raw[1] = report_buf[7 + (isLeft ? 0 : 3)];
                stick_raw[2] = report_buf[8 + (isLeft ? 0 : 3)];

                if (isPro) {
                    stick2_raw[0] = report_buf[6 + (!isLeft ? 0 : 3)];
                    stick2_raw[1] = report_buf[7 + (!isLeft ? 0 : 3)];
                    stick2_raw[2] = report_buf[8 + (!isLeft ? 0 : 3)];
                }

                stick_precal[0] = (UInt16)(stick_raw[0] | ((stick_raw[1] & 0xf) << 8));
                stick_precal[1] = (UInt16)((stick_raw[1] >> 4) | (stick_raw[2] << 4));
                CalibrationState.AddStickSample(this, false, stick_precal[0], stick_precal[1]);
                stick = CenterSticks(stick_precal, stick_cal, deadzone, isLeft ? stickScalingFactor : stickScalingFactor2);

                if (isPro) {
                    stick2_precal[0] = (UInt16)(stick2_raw[0] | ((stick2_raw[1] & 0xf) << 8));
                    stick2_precal[1] = (UInt16)((stick2_raw[1] >> 4) | (stick2_raw[2] << 4));
                    CalibrationState.AddStickSample(this, true, stick2_precal[0], stick2_precal[1]);
                    stick2 = CenterSticks(stick2_precal, stick2_cal, deadzone2, stickScalingFactor2);
                }

                // Read other Joycon's sticks
                if (isLeft && other != null && other != this) {
                    stick2 = otherStick;
                    other.otherStick = stick;
                }

                if (!isLeft && other != null && other != this) {
                    Array.Copy(stick, stick2, 2);
                    stick = otherStick;
                    other.otherStick = stick2;
                }
            }
            //

            // Set button states both for server and ViGEm
            lock (buttons) {
                lock (down_) {
                    for (int i = 0; i < buttons.Length; ++i) {
                        down_[i] = buttons[i];
                    }
                }
                buttons = new bool[20];

                buttons[(int)Button.DPAD_DOWN] = (report_buf[3 + (isLeft ? 2 : 0)] & (isLeft ? 0x01 : 0x04)) != 0;
                buttons[(int)Button.DPAD_RIGHT] = (report_buf[3 + (isLeft ? 2 : 0)] & (isLeft ? 0x04 : 0x08)) != 0;
                buttons[(int)Button.DPAD_UP] = (report_buf[3 + (isLeft ? 2 : 0)] & (isLeft ? 0x02 : 0x02)) != 0;
                buttons[(int)Button.DPAD_LEFT] = (report_buf[3 + (isLeft ? 2 : 0)] & (isLeft ? 0x08 : 0x01)) != 0;
                buttons[(int)Button.HOME] = ((report_buf[4] & 0x10) != 0);
                buttons[(int)Button.CAPTURE] = ((report_buf[4] & 0x20) != 0);
                buttons[(int)Button.MINUS] = ((report_buf[4] & 0x01) != 0);
                buttons[(int)Button.PLUS] = ((report_buf[4] & 0x02) != 0);
                buttons[(int)Button.STICK] = ((report_buf[4] & (isLeft ? 0x08 : 0x04)) != 0);
                buttons[(int)Button.SHOULDER_1] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x40) != 0;
                buttons[(int)Button.SHOULDER_2] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x80) != 0;
                buttons[(int)Button.SR] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x10) != 0;
                buttons[(int)Button.SL] = (report_buf[3 + (isLeft ? 2 : 0)] & 0x20) != 0;

                if (isPro) {
                    buttons[(int)Button.B] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x01 : 0x04)) != 0;
                    buttons[(int)Button.A] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x04 : 0x08)) != 0;
                    buttons[(int)Button.X] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x02 : 0x02)) != 0;
                    buttons[(int)Button.Y] = (report_buf[3 + (!isLeft ? 2 : 0)] & (!isLeft ? 0x08 : 0x01)) != 0;

                    buttons[(int)Button.STICK2] = ((report_buf[4] & (!isLeft ? 0x08 : 0x04)) != 0);
                    buttons[(int)Button.SHOULDER2_1] = (report_buf[3 + (!isLeft ? 2 : 0)] & 0x40) != 0;
                    buttons[(int)Button.SHOULDER2_2] = (report_buf[3 + (!isLeft ? 2 : 0)] & 0x80) != 0;
                }

                if (other != null && other != this) {
                    buttons[(int)(Button.B)] = other.buttons[(int)Button.DPAD_DOWN];
                    buttons[(int)(Button.A)] = other.buttons[(int)Button.DPAD_RIGHT];
                    buttons[(int)(Button.X)] = other.buttons[(int)Button.DPAD_UP];
                    buttons[(int)(Button.Y)] = other.buttons[(int)Button.DPAD_LEFT];

                    buttons[(int)Button.STICK2] = other.buttons[(int)Button.STICK];
                    buttons[(int)Button.SHOULDER2_1] = other.buttons[(int)Button.SHOULDER_1];
                    buttons[(int)Button.SHOULDER2_2] = other.buttons[(int)Button.SHOULDER_2];
                }

                if (isLeft && other != null && other != this) {
                    buttons[(int)Button.HOME] = other.buttons[(int)Button.HOME];
                    buttons[(int)Button.PLUS] = other.buttons[(int)Button.PLUS];
                }

                if (!isLeft && other != null && other != this) {
                    buttons[(int)Button.MINUS] = other.buttons[(int)Button.MINUS];
                }

                long timestamp = Stopwatch.GetTimestamp();

                lock (buttons_up) {
                    lock (buttons_down) {
                        bool changed = false;
                        for (int i = 0; i < buttons.Length; ++i) {
                            buttons_up[i] = (down_[i] & !buttons[i]);
                            buttons_down[i] = (!down_[i] & buttons[i]);
                            if (down_[i] != buttons[i])
                                buttons_down_timestamp[i] = (buttons[i] ? timestamp : -1);
                            if (buttons_up[i] || buttons_down[i])
                                changed = true;
                        }

                        inactivity = (changed) ? timestamp : inactivity;
                    }
                }
            }

            return 0;
        }

        // Get Gyro/Accel data
        private void ExtractIMUValues(byte[] report_buf, int n = 0) {
            if (!(isSnes || is64)) {
                // Must happen before this sample is transformed/added to either estimator. If a
                // join/split changed the basis, the orientation accumulated in the old basis is
                // invalid even though the physical controller itself never disconnected.
                EnsureGyroOrientationBasis();

                gyr_r[0] = (Int16)(report_buf[19 + n * 12] | ((report_buf[20 + n * 12] << 8) & 0xff00));
                gyr_r[1] = (Int16)(report_buf[21 + n * 12] | ((report_buf[22 + n * 12] << 8) & 0xff00));
                gyr_r[2] = (Int16)(report_buf[23 + n * 12] | ((report_buf[24 + n * 12] << 8) & 0xff00));
                acc_r[0] = (Int16)(report_buf[13 + n * 12] | ((report_buf[14 + n * 12] << 8) & 0xff00));
                acc_r[1] = (Int16)(report_buf[15 + n * 12] | ((report_buf[16 + n * 12] << 8) & 0xff00));
                acc_r[2] = (Int16)(report_buf[17 + n * 12] | ((report_buf[18 + n * 12] << 8) & 0xff00));

                if (Boolean.Parse(ConfigurationManager.AppSettings["AllowCalibration"])) {
                    for (int i = 0; i < 3; ++i) {
                        switch (i) {
                            case 0:
                                acc_g.X = (acc_r[i] - activeData[3]) * (1.0f / acc_sen[i]) * 4.0f;
                                gyr_g.X = (gyr_r[i] - activeData[0]) * (816.0f / gyr_sen[i]);
                                CalibrationState.AddSample(this, CalibrationState.XA, CalibrationState.XG, acc_r[i], gyr_r[i]);
                                break;
                            case 1:
                                acc_g.Y = (!isLeft ? -1 : 1) * (acc_r[i] - activeData[4]) * (1.0f / acc_sen[i]) * 4.0f;
                                gyr_g.Y = -(!isLeft ? -1 : 1) * (gyr_r[i] - activeData[1]) * (816.0f / gyr_sen[i]);
                                CalibrationState.AddSample(this, CalibrationState.YA, CalibrationState.YG, acc_r[i], gyr_r[i]);
                                break;
                            case 2:
                                acc_g.Z = (!isLeft ? -1 : 1) * (acc_r[i] - activeData[5]) * (1.0f / acc_sen[i]) * 4.0f;
                                gyr_g.Z = -(!isLeft ? -1 : 1) * (gyr_r[i] - activeData[2]) * (816.0f / gyr_sen[i]);
                                CalibrationState.AddSample(this, CalibrationState.ZA, CalibrationState.ZG, acc_r[i], gyr_r[i]);
                                break;
                        }
                    }
                } else {
                    Int16[] offset;
                    if (isPro)
                        offset = pro_hor_offset;
                    else if (isLeft)
                        offset = left_hor_offset;
                    else
                        offset = right_hor_offset;

                    for (int i = 0; i < 3; ++i) {
                        switch (i) {
                            case 0:
                                acc_g.X = (acc_r[i] - offset[i]) * (1.0f / (acc_sensiti[i] - acc_neutral[i])) * 4.0f;
                                gyr_g.X = (gyr_r[i] - gyr_neutral[i]) * (816.0f / (gyr_sensiti[i] - gyr_neutral[i]));

                                break;
                            case 1:
                                acc_g.Y = (!isLeft ? -1 : 1) * (acc_r[i] - offset[i]) * (1.0f / (acc_sensiti[i] - acc_neutral[i])) * 4.0f;
                                gyr_g.Y = -(!isLeft ? -1 : 1) * (gyr_r[i] - gyr_neutral[i]) * (816.0f / (gyr_sensiti[i] - gyr_neutral[i]));
                                break;
                            case 2:
                                acc_g.Z = (!isLeft ? -1 : 1) * (acc_r[i] - offset[i]) * (1.0f / (acc_sensiti[i] - acc_neutral[i])) * 4.0f;
                                gyr_g.Z = -(!isLeft ? -1 : 1) * (gyr_r[i] - gyr_neutral[i]) * (816.0f / (gyr_sensiti[i] - gyr_neutral[i]));
                                break;
                        }
                    }
                }

                // Capture a canonical, physically consistent IMU frame before BetterJoy's
                // legacy solo-controller transform mutates acc_g and gyr_g differently.
                UpdateCanonicalGyroMouseImu();

                if (other == null && !isPro) { // single joycon mode; Z do not swap, rest do
                    if (isLeft) {
                        acc_g.X = -acc_g.X;
                        acc_g.Y = -acc_g.Y;
                        gyr_g.X = -gyr_g.X;
                    } else {
                        gyr_g.Y = -gyr_g.Y;
                    }

                    float temp = acc_g.X;
                    acc_g.X = acc_g.Y;
                    acc_g.Y = -temp;

                    temp = gyr_g.X;
                    gyr_g.X = gyr_g.Y;
                    gyr_g.Y = temp;
                }

                // Update rotation Quaternion
                float deg_to_rad = 0.0174533f;
                AHRS.Update(gyr_g.X * deg_to_rad, gyr_g.Y * deg_to_rad, gyr_g.Z * deg_to_rad, acc_g.X, acc_g.Y, acc_g.Z);
            }
        }

        public void Begin() {
            if (PollThreadObj == null) {
                PollThreadObj = new Thread(new ThreadStart(Poll));
                PollThreadObj.IsBackground = true;
                PollThreadObj.Start();

                form.AppendTextBox("Starting poll thread.\r\n");
            } else {
                form.AppendTextBox("Poll cannot start.\r\n");
            }
        }

        // Should really be called calculating stick data
        private float[] CenterSticks(UInt16[] vals, ushort[] cal, ushort dz, float scaling_factor) {
            ushort[] t = cal;

            float[] s = { 0, 0 };
            float dx = vals[0] - t[2], dy = vals[1] - t[3];
            if (Math.Abs(dx * dx + dy * dy) < dz * dz)
                return s;

            s[0] = dx / (dx > 0 ? t[0] : t[4]);
            s[1] = dy / (dy > 0 ? t[1] : t[5]);

            if (scaling_factor != 1.0f) {
                s[0] *= scaling_factor;
                s[1] *= scaling_factor;

                s[0] = Math.Max(Math.Min(s[0], 1.0f), -1.0f);
                s[1] = Math.Max(Math.Min(s[1], 1.0f), -1.0f);
            }

            return s;
        }

        private static short CastStickValue(float stick_value) {
            return (short)Math.Max(Int16.MinValue, Math.Min(Int16.MaxValue, stick_value * (stick_value > 0 ? Int16.MaxValue : -Int16.MinValue)));
        }

        private static byte CastStickValueByte(float stick_value) {
            return (byte)Math.Max(Byte.MinValue, Math.Min(Byte.MaxValue, 127 - stick_value * Byte.MaxValue));
        }

        public void SetRumble(float low_freq, float high_freq, float amp) {
            if (state <= Joycon.state_.ATTACHED) return;
            rumble_obj.set_vals(low_freq, high_freq, amp);
        }

        private void SendRumble(byte[] buf) {
            byte[] buf_ = new byte[report_len];
            buf_[0] = 0x10;
            buf_[1] = global_count;
            if (global_count == 0xf) global_count = 0;
            else ++global_count;
            Array.Copy(buf, 0, buf_, 2, 8);
            PrintArray(buf_, DebugType.RUMBLE, format: "Rumble data sent: {0:S}");
            HIDapi.hid_write(handle, buf_, new UIntPtr(report_len));
        }

        private byte[] Subcommand(byte sc, byte[] buf, uint len, bool print = true) {
            byte[] buf_ = new byte[report_len];
            byte[] response = new byte[report_len];
            Array.Copy(default_buf, 0, buf_, 2, 8);
            Array.Copy(buf, 0, buf_, 11, len);
            buf_[10] = sc;
            buf_[1] = global_count;
            buf_[0] = 0x1;
            if (global_count == 0xf) global_count = 0;
            else ++global_count;
            if (print) { PrintArray(buf_, DebugType.COMMS, len, 11, "Subcommand 0x" + string.Format("{0:X2}", sc) + " sent. Data: 0x{0:S}"); };
            HIDapi.hid_write(handle, buf_, new UIntPtr(len + 11));
            int tries = 0;
            do {
                int res = HIDapi.hid_read_timeout(handle, response, new UIntPtr(report_len), 100);
                if (res < 1) DebugPrint("No response.", DebugType.COMMS);
                else if (print) { PrintArray(response, DebugType.COMMS, report_len - 1, 1, "Response ID 0x" + string.Format("{0:X2}", response[0]) + ". Data: 0x{0:S}"); }
                tries++;
            } while (tries < 10 && response[0] != 0x21 && response[14] != sc);

            return response;
        }

        private void dump_calibration_data() {
            if (isSnes || is64 || thirdParty) {
                short[] temp = (short[])ConfigurationManager.AppSettings["acc_sensiti"].Split(',').Select(s => short.Parse(s)).ToArray();
                acc_sensiti[0] = temp[0]; acc_sensiti[1] = temp[1]; acc_sensiti[2] = temp[2];
                temp = (short[])ConfigurationManager.AppSettings["gyr_sensiti"].Split(',').Select(s => short.Parse(s)).ToArray();
                gyr_sensiti[0] = temp[0]; gyr_sensiti[1] = temp[1]; gyr_sensiti[2] = temp[2];
                ushort[] temp2 = (ushort[])ConfigurationManager.AppSettings["stick_cal"].Split(',').Select(s => ushort.Parse(s.Substring(2), System.Globalization.NumberStyles.HexNumber)).ToArray();
                stick_cal[0] = temp2[0]; stick_cal[1] = temp2[1]; stick_cal[2] = temp2[2];
                stick_cal[3] = temp2[3]; stick_cal[4] = temp2[4]; stick_cal[5] = temp2[5];
                deadzone = ushort.Parse(ConfigurationManager.AppSettings["deadzone"]);
                temp2 = (ushort[])ConfigurationManager.AppSettings["stick2_cal"].Split(',').Select(s => ushort.Parse(s.Substring(2), System.Globalization.NumberStyles.HexNumber)).ToArray();
                stick2_cal[0] = temp2[0]; stick2_cal[1] = temp2[1]; stick2_cal[2] = temp2[2];
                stick2_cal[3] = temp2[3]; stick2_cal[4] = temp2[4]; stick2_cal[5] = temp2[5];
                deadzone2 = ushort.Parse(ConfigurationManager.AppSettings["deadzone2"]);
                getActiveStickData();
                return;
            }

            HIDapi.hid_set_nonblocking(handle, 0);
            byte[] buf_ = ReadSPI(0x80, (isLeft ? (byte)0x12 : (byte)0x1d), 9); // get user calibration data if possible
            bool found = false;
            for (int i = 0; i < 9; ++i) {
                if (buf_[i] != 0xff) {
                    form.AppendTextBox("Using user stick calibration data.\r\n");
                    found = true;
                    break;
                }
            }
            if (!found) {
                form.AppendTextBox("Using factory stick calibration data.\r\n");
                buf_ = ReadSPI(0x60, (isLeft ? (byte)0x3d : (byte)0x46), 9); // get user calibration data if possible
            }
            stick_cal[isLeft ? 0 : 2] = (UInt16)((buf_[1] << 8) & 0xF00 | buf_[0]); // X Axis Max above center
            stick_cal[isLeft ? 1 : 3] = (UInt16)((buf_[2] << 4) | (buf_[1] >> 4));  // Y Axis Max above center
            stick_cal[isLeft ? 2 : 4] = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]); // X Axis Center
            stick_cal[isLeft ? 3 : 5] = (UInt16)((buf_[5] << 4) | (buf_[4] >> 4));  // Y Axis Center
            stick_cal[isLeft ? 4 : 0] = (UInt16)((buf_[7] << 8) & 0xF00 | buf_[6]); // X Axis Min below center
            stick_cal[isLeft ? 5 : 1] = (UInt16)((buf_[8] << 4) | (buf_[7] >> 4));  // Y Axis Min below center

            PrintArray(stick_cal, len: 6, start: 0, format: "Stick calibration data: {0:S}");

            if (isPro) {
                buf_ = ReadSPI(0x80, (!isLeft ? (byte)0x12 : (byte)0x1d), 9); // get user calibration data if possible
                found = false;
                for (int i = 0; i < 9; ++i) {
                    if (buf_[i] != 0xff) {
                        form.AppendTextBox("Using user stick calibration data.\r\n");
                        found = true;
                        break;
                    }
                }
                if (!found) {
                    form.AppendTextBox("Using factory stick calibration data.\r\n");
                    buf_ = ReadSPI(0x60, (!isLeft ? (byte)0x3d : (byte)0x46), 9); // get user calibration data if possible
                }
                stick2_cal[!isLeft ? 0 : 2] = (UInt16)((buf_[1] << 8) & 0xF00 | buf_[0]); // X Axis Max above center
                stick2_cal[!isLeft ? 1 : 3] = (UInt16)((buf_[2] << 4) | (buf_[1] >> 4));  // Y Axis Max above center
                stick2_cal[!isLeft ? 2 : 4] = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]); // X Axis Center
                stick2_cal[!isLeft ? 3 : 5] = (UInt16)((buf_[5] << 4) | (buf_[4] >> 4));  // Y Axis Center
                stick2_cal[!isLeft ? 4 : 0] = (UInt16)((buf_[7] << 8) & 0xF00 | buf_[6]); // X Axis Min below center
                stick2_cal[!isLeft ? 5 : 1] = (UInt16)((buf_[8] << 4) | (buf_[7] >> 4));  // Y Axis Min below center

                PrintArray(stick2_cal, len: 6, start: 0, format: "Stick calibration data: {0:S}");

                buf_ = ReadSPI(0x60, (!isLeft ? (byte)0x86 : (byte)0x98), 16);
                deadzone2 = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]);
            }

            buf_ = ReadSPI(0x60, (isLeft ? (byte)0x86 : (byte)0x98), 16);
            deadzone = (UInt16)((buf_[4] << 8) & 0xF00 | buf_[3]);

            buf_ = ReadSPI(0x80, 0x28, 10);
            acc_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            acc_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            acc_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            buf_ = ReadSPI(0x80, 0x2E, 10);
            acc_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            acc_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            acc_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            buf_ = ReadSPI(0x80, 0x34, 10);
            gyr_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            gyr_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            gyr_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            buf_ = ReadSPI(0x80, 0x3A, 10);
            gyr_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
            gyr_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
            gyr_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

            PrintArray(gyr_neutral, len: 3, d: DebugType.IMU, format: "User gyro neutral position: {0:S}");

            // This is an extremely messy way of checking to see whether there is user stick calibration data present, but I've seen conflicting user calibration data on blank Joy-Cons. Worth another look eventually.
            if (gyr_neutral[0] + gyr_neutral[1] + gyr_neutral[2] == -3 || Math.Abs(gyr_neutral[0]) > 100 || Math.Abs(gyr_neutral[1]) > 100 || Math.Abs(gyr_neutral[2]) > 100) {
                buf_ = ReadSPI(0x60, 0x20, 10);
                acc_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                acc_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                acc_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                buf_ = ReadSPI(0x60, 0x26, 10);
                acc_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                acc_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                acc_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                buf_ = ReadSPI(0x60, 0x2C, 10);
                gyr_neutral[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                gyr_neutral[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                gyr_neutral[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                buf_ = ReadSPI(0x60, 0x32, 10);
                gyr_sensiti[0] = (Int16)(buf_[0] | ((buf_[1] << 8) & 0xff00));
                gyr_sensiti[1] = (Int16)(buf_[2] | ((buf_[3] << 8) & 0xff00));
                gyr_sensiti[2] = (Int16)(buf_[4] | ((buf_[5] << 8) & 0xff00));

                PrintArray(gyr_neutral, len: 3, d: DebugType.IMU, format: "Factory gyro neutral position: {0:S}");
            }
            HIDapi.hid_set_nonblocking(handle, 1);

            getActiveStickData();
        }

        private byte[] ReadSPI(byte addr1, byte addr2, uint len, bool print = false) {
            byte[] buf = { addr2, addr1, 0x00, 0x00, (byte)len };
            byte[] read_buf = new byte[len];
            byte[] buf_ = new byte[len + 20];

            for (int i = 0; i < 100; ++i) {
                buf_ = Subcommand(0x10, buf, 5, false);
                if (buf_[15] == addr2 && buf_[16] == addr1) {
                    break;
                }
            }
            Array.Copy(buf_, 20, read_buf, 0, len);
            if (print) PrintArray(read_buf, DebugType.COMMS, len);
            return read_buf;
        }

        private void PrintArray<T>(T[] arr, DebugType d = DebugType.NONE, uint len = 0, uint start = 0, string format = "{0:S}") {
            if (d != debug_type && debug_type != DebugType.ALL) return;
            if (len == 0) len = (uint)arr.Length;
            string tostr = "";
            for (int i = 0; i < len; ++i) {
                tostr += string.Format((arr[0] is byte) ? "{0:X2} " : ((arr[0] is float) ? "{0:F} " : "{0:D} "), arr[i + start]);
            }
            DebugPrint(string.Format(format, tostr), d);
        }


        private static float GetNormalizedValue(float value, float rawMin, float rawMax, float normalizedMin, float normalizedMax)
        {
            return (value - rawMin) / (rawMax - rawMin) * (normalizedMax - normalizedMin) + normalizedMin;
        }

        private static float[] Getn64StickValues(Joycon input)
        {
            var isLeft = input.isLeft;
            var other = input.other;
            var stick = input.stick;
            var stick2 = input.stick2;
            var stick_correction = new float[] { 0f, 0f};

            var xAxis = (other == input && !isLeft) ? stick2[0] : stick[0];
            var yAxis = (other == input && !isLeft) ? stick2[1] : stick[1];


            if (xAxis < input.minX)
            {
                input.minX = xAxis;
            }

            if (xAxis > input.maxX)
            {
                input.maxX = xAxis;
            }

            if (yAxis < input.minY)
            {
                input.minY = yAxis;
            }

            if (yAxis > input.maxY)
            {
                input.maxY = yAxis;
            }

            var middleX = (input.minX + (input.maxX - input.minX)/2);
            var middleY = (input.minY + (input.maxY - input.minY)/2);
            #if DEBUG
            var desc = "";
            desc += "x: "+xAxis+"; y: "+yAxis;
            desc += "\n X: ["+input.minX+", "+input.maxX+"]; Y: ["+input.minY+", "+input.maxY+"] ";
            desc += "; middle ["+middleX+", "+middleY+"]";
                
            Debug.WriteLine(desc);
            #endif

            var negative_normalized = new float[] {-1, 0};
            var positive_normalized = new float[] {0, 1};

            var xRange = new float[] {-1f, 1f};
            var yRange = new float[] {-1f, 1f};

            if (input.realn64Range)
            {
                xRange = new float[] {-0.79f, 0.79f};
                yRange = new float[] {-0.79f, 0.79f};
            }
            

            if (xAxis < (middleX - middleX))
            {
                stick_correction[0] = GetNormalizedValue(xAxis, input.minX, (middleX - middleX), xRange[0], 0f);
            }

            if (xAxis > (middleX+middleX))
            {
                stick_correction[0] = GetNormalizedValue(xAxis, (middleX+middleX), input.maxX, 0f, xRange[1]);
            }

            if (yAxis < (middleY-middleY))
            {
                stick_correction[1] = GetNormalizedValue(yAxis, input.minY, (middleY-middleY), yRange[0], 0f);
            }

            if (yAxis > (middleY+middleY))
            {
                stick_correction[1] = GetNormalizedValue(yAxis, (middleY+middleY), input.maxY, 0f, yRange[1]);
            }


            return stick_correction;
        }

        private static OutputControllerXbox360InputState MapToXbox360Input(Joycon input) {
            var output = new OutputControllerXbox360InputState();


            var swapAB = input.swapAB;
            var swapXY = input.swapXY;

            var isPro = input.isPro;
            var isLeft = input.isLeft;
            var isSnes = input.isSnes;
            var is64 = input.is64;
            var other = input.other;
            var GyroAnalogSliders = input.GyroAnalogSliders;

            var buttons = input.buttons;
            var stick = input.stick;
            var stick2 = input.stick2;
            var sliderVal = input.sliderVal;

            if (is64)
            {
                output.axis_right_x = (short) ((buttons[(int)Button.X] ? Int16.MinValue : 0) + (buttons[(int)Button.MINUS] ? Int16.MaxValue : 0));
                output.axis_right_y = (short) ((buttons[(int)Button.SHOULDER2_2] ? Int16.MinValue: 0) + (buttons[(int)Button.Y] ? Int16.MaxValue: 0));

                var n64Stick = Getn64StickValues(input);

                output.axis_left_x = CastStickValue(n64Stick[0]);
                output.axis_left_y = CastStickValue(n64Stick[1]);

                output.start = buttons[(int)Button.PLUS];
                output.a = buttons[(int)(!swapAB ? Button.B : Button.A)];
                output.b = buttons[(int)(!swapAB ? Button.A : Button.B)];

                output.shoulder_left = buttons[(int)Button.SHOULDER_1];
                output.shoulder_right = buttons[(int)Button.SHOULDER2_1];

                output.trigger_left = (byte)(buttons[(int)Button.SHOULDER_2] ? Byte.MaxValue : 0);
                output.trigger_right = (byte)(buttons[(int)Button.STICK] ? Byte.MaxValue : 0);

                output.dpad_down = buttons[(int)Button.DPAD_DOWN];
                output.dpad_left = buttons[(int)Button.DPAD_LEFT];
                output.dpad_right = buttons[(int)Button.DPAD_RIGHT];
                output.dpad_up = buttons[(int)Button.DPAD_UP];
                output.guide = buttons[(int)Button.HOME];

            }
            else if (isPro) {
                output.a = buttons[(int)(!swapAB ? Button.B : Button.A)];
                output.b = buttons[(int)(!swapAB ? Button.A : Button.B)];
                output.y = buttons[(int)(!swapXY ? Button.X : Button.Y)];
                output.x = buttons[(int)(!swapXY ? Button.Y : Button.X)];

                output.dpad_up = buttons[(int)Button.DPAD_UP];
                output.dpad_down = buttons[(int)Button.DPAD_DOWN];
                output.dpad_left = buttons[(int)Button.DPAD_LEFT];
                output.dpad_right = buttons[(int)Button.DPAD_RIGHT];

                output.back = buttons[(int)Button.MINUS];
                output.start = buttons[(int)Button.PLUS];
                output.guide = buttons[(int)Button.HOME];

                output.shoulder_left = buttons[(int)Button.SHOULDER_1];
                output.shoulder_right = buttons[(int)Button.SHOULDER2_1];

                output.thumb_stick_left = buttons[(int)Button.STICK];
                output.thumb_stick_right = buttons[(int)Button.STICK2];
            } else {
                if (other != null) { // no need for && other != this
                    output.a = buttons[(int)(!swapAB ? isLeft ? Button.B : Button.DPAD_DOWN : isLeft ? Button.A : Button.DPAD_RIGHT)];
                    output.b = buttons[(int)(swapAB ? isLeft ? Button.B : Button.DPAD_DOWN : isLeft ? Button.A : Button.DPAD_RIGHT)];
                    output.y = buttons[(int)(!swapXY ? isLeft ? Button.X : Button.DPAD_UP : isLeft ? Button.Y : Button.DPAD_LEFT)];
                    output.x = buttons[(int)(swapXY ? isLeft ? Button.X : Button.DPAD_UP : isLeft ? Button.Y : Button.DPAD_LEFT)];

                    output.dpad_up = buttons[(int)(isLeft ? Button.DPAD_UP : Button.X)];
                    output.dpad_down = buttons[(int)(isLeft ? Button.DPAD_DOWN : Button.B)];
                    output.dpad_left = buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.Y)];
                    output.dpad_right = buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.A)];

                    output.back = buttons[(int)Button.MINUS];
                    output.start = buttons[(int)Button.PLUS];
                    output.guide = buttons[(int)Button.HOME];

                    output.shoulder_left = buttons[(int)(isLeft ? Button.SHOULDER_1 : Button.SHOULDER2_1)];
                    output.shoulder_right = buttons[(int)(isLeft ? Button.SHOULDER2_1 : Button.SHOULDER_1)];

                    output.thumb_stick_left = buttons[(int)(isLeft ? Button.STICK : Button.STICK2)];
                    output.thumb_stick_right = buttons[(int)(isLeft ? Button.STICK2 : Button.STICK)];
                } else { // single joycon mode
                    output.a = buttons[(int)(!swapAB ? isLeft ? Button.DPAD_LEFT : Button.DPAD_RIGHT : isLeft ? Button.DPAD_DOWN : Button.DPAD_UP)];
                    output.b = buttons[(int)(swapAB ? isLeft ? Button.DPAD_LEFT : Button.DPAD_RIGHT : isLeft ? Button.DPAD_DOWN : Button.DPAD_UP)];
                    output.y = buttons[(int)(!swapXY ? isLeft ? Button.DPAD_RIGHT : Button.DPAD_LEFT : isLeft ? Button.DPAD_UP : Button.DPAD_DOWN)];
                    output.x = buttons[(int)(swapXY ? isLeft ? Button.DPAD_RIGHT : Button.DPAD_LEFT : isLeft ? Button.DPAD_UP : Button.DPAD_DOWN)];

                    output.back = buttons[(int)Button.MINUS] | buttons[(int)Button.HOME];
                    output.start = buttons[(int)Button.PLUS] | buttons[(int)Button.CAPTURE];

                    output.shoulder_left = buttons[(int)Button.SL];
                    output.shoulder_right = buttons[(int)Button.SR];

                    output.thumb_stick_left = buttons[(int)Button.STICK];
                }
            }

            // overwrite guide button if it's custom-mapped
            if (Config.Value("home") != "0")
                output.guide = false;

            if (!(isSnes || is64)) {
                if (other != null || isPro) { // no need for && other != this
                    output.axis_left_x = CastStickValue((other == input && !isLeft) ? stick2[0] : stick[0]);
                    output.axis_left_y = CastStickValue((other == input && !isLeft) ? stick2[1] : stick[1]);

                    output.axis_right_x = CastStickValue((other == input && !isLeft) ? stick[0] : stick2[0]);
                    output.axis_right_y = CastStickValue((other == input && !isLeft) ? stick[1] : stick2[1]);
                } else { // single joycon mode
                    output.axis_left_y = CastStickValue((isLeft ? 1 : -1) * stick[0]);
                    output.axis_left_x = CastStickValue((isLeft ? -1 : 1) * stick[1]);
                }
            }

            if (!is64)
            {
                if (other != null || isPro) {
                    byte lval = GyroAnalogSliders ? sliderVal[0] : Byte.MaxValue;
                    byte rval = GyroAnalogSliders ? sliderVal[1] : Byte.MaxValue;
                    output.trigger_left = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_2 : Button.SHOULDER2_2)] ? lval : 0);
                    output.trigger_right = (byte)(buttons[(int)(isLeft ? Button.SHOULDER2_2 : Button.SHOULDER_2)] ? rval : 0);
                } else {
                    output.trigger_left = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_2 : Button.SHOULDER_1)] ? Byte.MaxValue : 0);
                    output.trigger_right = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_1 : Button.SHOULDER_2)] ? Byte.MaxValue : 0);
                }
            }

            return output;
        }

        public static OutputControllerDualShock4InputState MapToDualShock4Input(Joycon input) {
            var output = new OutputControllerDualShock4InputState();

            var swapAB = input.swapAB;
            var swapXY = input.swapXY;

            var isPro = input.isPro;
            var isLeft = input.isLeft;
            var isSnes = input.isSnes;
            var is64 = input.is64;
            var other = input.other;
            var GyroAnalogSliders = input.GyroAnalogSliders;

            var buttons = input.buttons;
            var stick = input.stick;
            var stick2 = input.stick2;
            var sliderVal = input.sliderVal;

            if (is64)
            {
                output.thumb_right_x = (byte) ((buttons[(int)Button.X] ? Byte.MinValue : 0) + (buttons[(int)Button.MINUS] ? Byte.MaxValue : 0));
                output.thumb_right_y = (byte) ((buttons[(int)Button.SHOULDER2_2] ? Byte.MinValue: 0) + (buttons[(int)Button.Y] ? Byte.MaxValue: 0));

                output.thumb_left_x = CastStickValueByte((other == input && !isLeft) ? -stick2[0] : -stick[0]);
                output.thumb_left_y = CastStickValueByte((other == input && !isLeft) ? stick2[1] : stick[1]);

                output.options = buttons[(int)Button.PLUS];
                output.cross = buttons[(int)(!swapAB ? Button.B : Button.A)];
                output.circle = buttons[(int)(!swapAB ? Button.A : Button.B)];

                output.shoulder_left = buttons[(int)Button.SHOULDER_1];
                output.shoulder_right = buttons[(int)Button.SHOULDER2_1];

                output.trigger_left = buttons[(int)Button.SHOULDER_2];
                output.trigger_right = buttons[(int)Button.STICK];
                output.trigger_left_value = (byte)(buttons[(int)Button.SHOULDER_2] ? Byte.MaxValue : 0);
                output.trigger_right_value = (byte)(buttons[(int)Button.STICK] ? Byte.MaxValue : 0);


                if (buttons[(int)Button.DPAD_UP]) {
                    if (buttons[(int)Button.DPAD_LEFT])
                        output.dPad = DpadDirection.Northwest;
                    else if (buttons[(int)Button.DPAD_RIGHT])
                        output.dPad = DpadDirection.Northeast;
                    else
                        output.dPad = DpadDirection.North;
                } else if (buttons[(int)Button.DPAD_DOWN]) {
                    if (buttons[(int)Button.DPAD_LEFT])
                        output.dPad = DpadDirection.Southwest;
                    else if (buttons[(int)Button.DPAD_RIGHT])
                        output.dPad = DpadDirection.Southeast;
                    else
                        output.dPad = DpadDirection.South;
                } else if (buttons[(int)Button.DPAD_LEFT])
                    output.dPad = DpadDirection.West;
                else if (buttons[(int)Button.DPAD_RIGHT])
                    output.dPad = DpadDirection.East;                
            }

            if (isPro) {
                output.cross = buttons[(int)(!swapAB ? Button.B : Button.A)];
                output.circle = buttons[(int)(!swapAB ? Button.A : Button.B)];
                output.triangle = buttons[(int)(!swapXY ? Button.X : Button.Y)];
                output.square = buttons[(int)(!swapXY ? Button.Y : Button.X)];


                if (buttons[(int)Button.DPAD_UP]) {
                    if (buttons[(int)Button.DPAD_LEFT])
                        output.dPad = DpadDirection.Northwest;
                    else if (buttons[(int)Button.DPAD_RIGHT])
                        output.dPad = DpadDirection.Northeast;
                    else
                        output.dPad = DpadDirection.North;
                } else if (buttons[(int)Button.DPAD_DOWN]) {
                    if (buttons[(int)Button.DPAD_LEFT])
                        output.dPad = DpadDirection.Southwest;
                    else if (buttons[(int)Button.DPAD_RIGHT])
                        output.dPad = DpadDirection.Southeast;
                    else
                        output.dPad = DpadDirection.South;
                } else if (buttons[(int)Button.DPAD_LEFT])
                    output.dPad = DpadDirection.West;
                else if (buttons[(int)Button.DPAD_RIGHT])
                    output.dPad = DpadDirection.East;

                output.share = buttons[(int)Button.CAPTURE];
                output.options = buttons[(int)Button.PLUS];
                output.ps = buttons[(int)Button.HOME];
                output.touchpad = buttons[(int)Button.MINUS];
                output.shoulder_left = buttons[(int)Button.SHOULDER_1];
                output.shoulder_right = buttons[(int)Button.SHOULDER2_1];
                output.thumb_left = buttons[(int)Button.STICK];
                output.thumb_right = buttons[(int)Button.STICK2];
            } else {
                if (other != null) { // no need for && other != this
                    output.cross = !swapAB ? buttons[(int)(isLeft ? Button.B : Button.DPAD_DOWN)] : buttons[(int)(isLeft ? Button.A : Button.DPAD_RIGHT)];
                    output.circle = swapAB ? buttons[(int)(isLeft ? Button.B : Button.DPAD_DOWN)] : buttons[(int)(isLeft ? Button.A : Button.DPAD_RIGHT)];
                    output.triangle = !swapXY ? buttons[(int)(isLeft ? Button.X : Button.DPAD_UP)] : buttons[(int)(isLeft ? Button.Y : Button.DPAD_LEFT)];
                    output.square = swapXY ? buttons[(int)(isLeft ? Button.X : Button.DPAD_UP)] : buttons[(int)(isLeft ? Button.Y : Button.DPAD_LEFT)];

                    if (buttons[(int)(isLeft ? Button.DPAD_UP : Button.X)])
                        if (buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.Y)])
                            output.dPad = DpadDirection.Northwest;
                        else if (buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.A)])
                            output.dPad = DpadDirection.Northeast;
                        else
                            output.dPad = DpadDirection.North;
                    else if (buttons[(int)(isLeft ? Button.DPAD_DOWN : Button.B)])
                        if (buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.Y)])
                            output.dPad = DpadDirection.Southwest;
                        else if (buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.A)])
                            output.dPad = DpadDirection.Southeast;
                        else
                            output.dPad = DpadDirection.South;
                    else if (buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.Y)])
                        output.dPad = DpadDirection.West;
                    else if (buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.A)])
                        output.dPad = DpadDirection.East;

                    output.share = buttons[(int)Button.CAPTURE];
                    output.options = buttons[(int)Button.PLUS];
                    output.ps = buttons[(int)Button.HOME];
                    output.touchpad = buttons[(int)Button.MINUS];
                    output.shoulder_left = buttons[(int)(isLeft ? Button.SHOULDER_1 : Button.SHOULDER2_1)];
                    output.shoulder_right = buttons[(int)(isLeft ? Button.SHOULDER2_1 : Button.SHOULDER_1)];
                    output.thumb_left = buttons[(int)(isLeft ? Button.STICK : Button.STICK2)];
                    output.thumb_right = buttons[(int)(isLeft ? Button.STICK2 : Button.STICK)];
                } else { // single joycon mode
                    output.cross = !swapAB ? buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.DPAD_RIGHT)] : buttons[(int)(isLeft ? Button.DPAD_DOWN : Button.DPAD_UP)];
                    output.circle = swapAB ? buttons[(int)(isLeft ? Button.DPAD_LEFT : Button.DPAD_RIGHT)] : buttons[(int)(isLeft ? Button.DPAD_DOWN : Button.DPAD_UP)];
                    output.triangle = !swapXY ? buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.DPAD_LEFT)] : buttons[(int)(isLeft ? Button.DPAD_UP : Button.DPAD_DOWN)];
                    output.square = swapXY ? buttons[(int)(isLeft ? Button.DPAD_RIGHT : Button.DPAD_LEFT)] : buttons[(int)(isLeft ? Button.DPAD_UP : Button.DPAD_DOWN)];

                    output.ps = buttons[(int)Button.MINUS] | buttons[(int)Button.HOME];
                    output.options = buttons[(int)Button.PLUS] | buttons[(int)Button.CAPTURE];

                    output.shoulder_left = buttons[(int)Button.SL];
                    output.shoulder_right = buttons[(int)Button.SR];

                    output.thumb_left = buttons[(int)Button.STICK];
                }
            }

            // overwrite guide button if it's custom-mapped
            if (Config.Value("home") != "0")
                output.ps = false;

            if (!(isSnes || is64)) {
                if (other != null || isPro) { // no need for && other != this
                    output.thumb_left_x = CastStickValueByte((other == input && !isLeft) ? -stick2[0] : -stick[0]);
                    output.thumb_left_y = CastStickValueByte((other == input && !isLeft) ? stick2[1] : stick[1]);
                    output.thumb_right_x = CastStickValueByte((other == input && !isLeft) ? -stick[0] : -stick2[0]);
                    output.thumb_right_y = CastStickValueByte((other == input && !isLeft) ? stick[1] : stick2[1]);
                } else { // single joycon mode
                    output.thumb_left_y = CastStickValueByte((isLeft ? 1 : -1) * stick[0]);
                    output.thumb_left_x = CastStickValueByte((isLeft ? 1 : -1) * stick[1]);
                }
            }

            if (!is64)
            {
                if (other != null || isPro) {
                    byte lval = GyroAnalogSliders ? sliderVal[0] : Byte.MaxValue;
                    byte rval = GyroAnalogSliders ? sliderVal[1] : Byte.MaxValue;
                    output.trigger_left_value = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_2 : Button.SHOULDER2_2)] ? lval : 0);
                    output.trigger_right_value = (byte)(buttons[(int)(isLeft ? Button.SHOULDER2_2 : Button.SHOULDER_2)] ? rval : 0);
                } else {
                    output.trigger_left_value = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_2 : Button.SHOULDER_1)] ? Byte.MaxValue : 0);
                    output.trigger_right_value = (byte)(buttons[(int)(isLeft ? Button.SHOULDER_1 : Button.SHOULDER_2)] ? Byte.MaxValue : 0);
                }
            // Output digital L2 / R2 in addition to analog L2 / R2
            output.trigger_left = output.trigger_left_value > 0 ? output.trigger_left = true : output.trigger_left = false;
            output.trigger_right = output.trigger_right_value > 0 ? output.trigger_right = true : output.trigger_right = false;
            }

            return output;
        }
    }
}
