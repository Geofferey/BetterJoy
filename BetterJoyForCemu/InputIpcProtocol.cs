using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace BetterJoyForCemu {
    // Message types carried over the named pipe between BetterJoyService (Session 0, no
    // desktop) and the session-launched input helper (has one) - see HeadlessJoyconHost for the
    // service side and InputHelper for the helper side.
    public enum InputMessageType : byte {
        // Helper -> service: raw captured events, forwarded on as-is for Program.OnKeyDown/
        // OnKeyUp/OnMouseButtonDown/OnMouseButtonUp to make the actual reset_mouse/active_gyro
        // bind decisions - the helper itself has no config/decision logic at all.
        KeyDown = 1,
        KeyUp = 2,
        MouseButtonDown = 3,
        MouseButtonUp = 4,

        // Service -> helper: one message per IJoyconHost Simulate* method.
        SimulateKeyClick = 10,
        SimulateKeyHold = 11,
        SimulateKeyRelease = 12,
        SimulateButtonClick = 13,
        SimulateButtonHold = 14,
        SimulateButtonRelease = 15,
        SimulateMoveTo = 16,
        SimulateMoveBy = 17,
        SimulateMoveToScreenCenter = 18,
        SimulateScroll = 19, // A: 1 = scroll up (Forwards), 0 = scroll down (Backwards)
        SimulateCursorMoveBy = 20, // Exact pixel delta; bypasses Windows relative-pointer scaling.
    }

    // Fixed-size (9 byte) message: 1 byte type + two 4-byte ints. Simple and robust over a
    // reliable local/cross-session pipe - no need for length-prefixing or variable framing.
    // Unused fields (e.g. B for a single-code message) are just 0.
    public struct InputMessage {
        public InputMessageType Type;
        public int A;
        public int B;

        public void WriteTo(BinaryWriter writer) {
            writer.Write((byte)Type);
            writer.Write(A);
            writer.Write(B);
        }

        public static InputMessage ReadFrom(BinaryReader reader) {
            return new InputMessage {
                Type = (InputMessageType)reader.ReadByte(),
                A = reader.ReadInt32(),
                B = reader.ReadInt32(),
            };
        }
    }

    public static class InputIpc {
        public const string PipeNamePrefix = "BetterJoyInputHelper_";

        // The pipe crosses session boundaries (service in Session 0, helper/GUI in the
        // interactive session), so the default ACL - which for a SYSTEM-created pipe would not
        // otherwise grant a regular user access - needs to be widened explicitly.
        public static PipeSecurity CreateCrossSessionPipeSecurity() {
            var security = new PipeSecurity();

            var authenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            security.AddAccessRule(new PipeAccessRule(authenticatedUsers, PipeAccessRights.ReadWrite, AccessControlType.Allow));

            // A custom DACL REPLACES the default one entirely rather than adding to it - without
            // an explicit grant here, the service's own account (SYSTEM) has no listed rights on
            // its own pipe. That doesn't stop the first CreateNamedPipe call (which establishes
            // the object), but every instance after that - the normal case for a long-lived,
            // reconnectable server pipe like this one - fails with UnauthorizedAccessException,
            // since creating an additional instance of an existing named pipe requires rights on
            // the existing object too, not just on connecting to it as a client.
            SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User;
            security.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));

            return security;
        }
    }
}
