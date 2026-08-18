using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

namespace BetterJoyForCemu {
    internal enum VirtualGameControllerType {
        Xbox360,
        DualShock4,
    }

    // Opens the legacy Game Controllers UI and, for a live selected profile, advances into that
    // controller's Properties dialog. Generic ViGEm targets do not provide their own DirectInput
    // control panel: IDirectInputDevice8.RunControlPanel therefore stops at the joy.cpl list just
    // like the global call. We still use DirectInput enumeration to resolve the selected virtual
    // device's list position, then use Windows UI Automation to select it and invoke Properties.
    internal static class GameControllerControlPanel {
        private const uint DirectInputVersion = 0x0800;
        private const uint DeviceClassGameController = 4;
        private const uint AttachedOnly = 1;
        private const int DirectInputEnumDevicesSlot = 4;

        private static readonly Guid DirectInput8InterfaceId =
            new Guid("BF798031-483A-4DA2-AA99-5D64ED369700");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DirectInputDeviceInstance {
            public uint Size;
            public Guid InstanceGuid;
            public Guid ProductGuid;
            public uint DeviceType;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string InstanceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ProductName;
            public Guid ForceFeedbackDriverGuid;
            public ushort UsagePage;
            public ushort Usage;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool EnumDevicesCallback(IntPtr instance, IntPtr context);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumDevicesDelegate(IntPtr directInput, uint deviceType,
                                                   EnumDevicesCallback callback,
                                                   IntPtr context, uint flags);

        [DllImport("dinput8.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int DirectInput8Create(IntPtr instance, uint version,
                                                      ref Guid interfaceId,
                                                      out IntPtr directInput,
                                                      IntPtr outerUnknown);

        public static bool OpenForVirtualController(VirtualGameControllerType controllerType,
                                                    int preferredOrdinal) {
            IntPtr directInput = IntPtr.Zero;
            try {
                Guid interfaceId = DirectInput8InterfaceId;
                IntPtr moduleInstance = Marshal.GetHINSTANCE(typeof(GameControllerControlPanel).Module);
                if (DirectInput8Create(moduleInstance, DirectInputVersion, ref interfaceId,
                                       out directInput, IntPtr.Zero) < 0 ||
                    directInput == IntPtr.Zero)
                    return false;

                var matchingControllerIndices = new List<int>();
                int controllerIndex = 0;
                EnumDevicesCallback callback = (instance, context) => {
                    DirectInputDeviceInstance candidate =
                        (DirectInputDeviceInstance)Marshal.PtrToStructure(
                            instance, typeof(DirectInputDeviceInstance));
                    if (IsRequestedVirtualController(candidate, controllerType))
                        matchingControllerIndices.Add(controllerIndex);
                    controllerIndex++;
                    return true;
                };

                EnumDevicesDelegate enumerate = GetMethod<EnumDevicesDelegate>(
                    directInput, DirectInputEnumDevicesSlot);
                if (enumerate(directInput, DeviceClassGameController, callback,
                              IntPtr.Zero, AttachedOnly) < 0 ||
                    matchingControllerIndices.Count == 0)
                    return false;

                int ordinal = preferredOrdinal >= 0 &&
                              preferredOrdinal < matchingControllerIndices.Count
                    ? preferredOrdinal
                    : 0;
                OpenDefault();
                OpenPropertiesWhenReady(matchingControllerIndices[ordinal]);
                return true;
            } catch {
                return false;
            } finally {
                if (directInput != IntPtr.Zero)
                    Marshal.Release(directInput);
            }
        }

        public static void OpenDefault() {
            Process.Start(new ProcessStartInfo {
                FileName = "control.exe",
                Arguments = "joy.cpl",
                UseShellExecute = true,
            });
        }

        private static void OpenPropertiesWhenReady(int controllerIndex) {
            var automationThread = new Thread(() => {
                // control.exe delegates to the Control Panel host, so its Process object is not a
                // reliable way to find the resulting window. Locate the top-level UI by shape:
                // joy.cpl has controller list items and a Properties button. This also works when
                // Windows reuses an already-open Game Controllers window.
                for (int attempt = 0; attempt < 50; attempt++) {
                    Thread.Sleep(100);
                    try {
                        AutomationElementCollection windows = AutomationElement.RootElement.FindAll(
                            TreeScope.Children,
                            new PropertyCondition(AutomationElement.ControlTypeProperty,
                                                  ControlType.Window));
                        foreach (AutomationElement window in windows) {
                            AutomationElement propertiesButton = FindPropertiesButton(window);
                            if (propertiesButton == null)
                                continue;

                            AutomationElementCollection controllers = window.FindAll(
                                TreeScope.Descendants,
                                new OrCondition(
                                    new PropertyCondition(AutomationElement.ControlTypeProperty,
                                                          ControlType.ListItem),
                                    new PropertyCondition(AutomationElement.ControlTypeProperty,
                                                          ControlType.DataItem)));
                            if (controllerIndex < 0 || controllerIndex >= controllers.Count)
                                continue;

                            SelectionItemPattern selection = controllers[controllerIndex]
                                .GetCurrentPattern(SelectionItemPattern.Pattern)
                                as SelectionItemPattern;
                            InvokePattern invoke = propertiesButton
                                .GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
                            if (selection == null || invoke == null)
                                continue;

                            selection.Select();
                            window.SetFocus();
                            Thread.Sleep(100);
                            invoke.Invoke();
                            return;
                        }
                    } catch (ElementNotAvailableException) {
                        // The Control Panel host was still replacing/refreshing its window.
                    } catch (InvalidOperationException) {
                        // UIA patterns can be temporarily unavailable while the list populates.
                    }
                }
            }) {
                IsBackground = true,
                Name = "JoyCplPropertiesLauncher",
            };
            automationThread.SetApartmentState(ApartmentState.STA);
            automationThread.Start();
        }

        private static AutomationElement FindPropertiesButton(AutomationElement window) {
            AutomationElementCollection buttons = window.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty,
                                      ControlType.Button));
            foreach (AutomationElement button in buttons) {
                if (Contains(button.Current.Name, "Properties"))
                    return button;
            }
            return null;
        }

        private static bool IsRequestedVirtualController(
            DirectInputDeviceInstance candidate, VirtualGameControllerType controllerType) {
            byte[] productGuid = candidate.ProductGuid.ToByteArray();
            ushort vendorId = BitConverter.ToUInt16(productGuid, 0);
            ushort productId = BitConverter.ToUInt16(productGuid, 2);

            if (controllerType == VirtualGameControllerType.Xbox360) {
                return (vendorId == 0x045e && productId == 0x028e) ||
                    Contains(candidate.ProductName, "XBOX 360");
            }

            return (vendorId == 0x054c && (productId == 0x05c4 || productId == 0x09cc)) ||
                String.Equals(candidate.ProductName, "Wireless Controller",
                              StringComparison.OrdinalIgnoreCase);
        }

        private static bool Contains(string value, string fragment) {
            return value != null &&
                value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static T GetMethod<T>(IntPtr comObject, int slot) where T : class {
            IntPtr vtable = Marshal.ReadIntPtr(comObject);
            IntPtr method = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer(method, typeof(T)) as T;
        }
    }
}
