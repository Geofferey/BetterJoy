using System;
using System.Numerics;

namespace BetterJoyForCemu {
    // Gravity tracking and "Player Space" gyro mapping adapted from Julian "Jibb" Smart's
    // MIT-licensed GamepadMotionHelpers reference implementation:
    // https://github.com/JibbSmart/GamepadMotionHelpers
    //
    // The important separation is that accelerometer fusion estimates which way is down, while
    // only calibrated gyro angular velocity creates pointer motion. Player Space uses gravity
    // loosely to blend the local yaw and roll axes, then caps the result at the actual angular
    // speed. An imperfect gravity estimate can therefore change the blend, but cannot manufacture
    // mouse movement or amplify it beyond motion measured by the gyro.
    internal sealed class GyroMousePlayerSpace {
        private const float DegreesToRadians = (float)(Math.PI / 180.0);
        // JoyShockMapper uses a 60-degree transition buffer for Player Turn. The speed cap below
        // still prevents the gravity blend from inventing angular velocity.
        private const float YawRelaxFactor = 2.0f;

        // GamepadMotionHelpers gravity-correction defaults (version 10).
        private const float ShakinessMinThreshold = 0.01f;
        private const float ShakinessMaxThreshold = 0.4f;
        private const float StillCorrectionSpeed = 1.0f;
        private const float ShakyCorrectionSpeed = 0.1f;
        private const float GyroCorrectionFactor = 0.1f;
        private const float GyroCorrectionMinThreshold = 0.05f;
        private const float GyroCorrectionMaxThreshold = 0.25f;
        private const float MinimumCorrectionSpeed = 0.01f;
        private const float ShortSteadinessHalfTime = 0.25f;

        private Vector3 gravity;
        private Vector3 smoothedAccel;
        private float shakiness;
        private bool gravityInitialized;

        public void Reset() {
            gravity = Vector3.Zero;
            smoothedAccel = Vector3.Zero;
            shakiness = 0.0f;
            gravityInitialized = false;
        }

        public void Update(Vector3 gyroDegPerSec, Vector3 accel, float deltaTime) {
            float accelMagnitude = accel.Length();
            Vector3 rotationRadians = gyroDegPerSec * DegreesToRadians;
            float angularSpeed = rotationRadians.Length();

            // BetterJoy has already transformed and calibrated this sample into the controller's
            // active coordinate basis. Seed gravity from it immediately instead of spending the
            // reference implementation's first second easing a zero vector toward "down". This
            // removes the slow correction users could feel after attach or a Joy-Con layout
            // change; the guarded fusion below still handles subsequent accumulated error.
            if (!gravityInitialized && accelMagnitude > 0.0f) {
                gravity = -accel / accelMagnitude;
                smoothedAccel = accel;
                shakiness = 0.0f;
                gravityInitialized = true;
                return;
            }

            // Gravity is world-fixed, so in controller-local coordinates it rotates opposite the
            // controller. Doing this from gyro preserves immediate response while accelerometer
            // correction below only reins in accumulated tilt error.
            gravity = RotateByInverseLocalMotion(gravity, rotationRadians, deltaTime);

            if (accelMagnitude <= 0.0f)
                return;

            smoothedAccel = RotateByInverseLocalMotion(smoothedAccel, rotationRadians, deltaTime);
            float smoothFactor = (float)Math.Pow(2.0, -deltaTime / ShortSteadinessHalfTime);
            shakiness *= smoothFactor;
            shakiness = Math.Max(shakiness, (accel - smoothedAccel).Length());
            smoothedAccel = Vector3.Lerp(accel, smoothedAccel, smoothFactor);

            // BetterJoy's accelerometer reports apparent gravity upward at rest, so the physical
            // down/gravity vector is its negation. Its calibrated nominal magnitude is 1 g.
            Vector3 targetGravity = -Vector3.Normalize(accel);
            Vector3 gravityError = targetGravity - gravity;
            float errorLength = gravityError.Length();
            if (errorLength <= 0.0f)
                return;

            float correctionSpeed = StillCorrectionSpeed +
                (ShakyCorrectionSpeed - StillCorrectionSpeed) *
                Clamp01((shakiness - ShakinessMinThreshold) /
                        (ShakinessMaxThreshold - ShakinessMinThreshold));

            float gyroCorrectionLimit = Math.Max(angularSpeed * GyroCorrectionFactor,
                                                  MinimumCorrectionSpeed);
            if (correctionSpeed > gyroCorrectionLimit) {
                float closeEnoughFactor = Clamp01((errorLength - GyroCorrectionMinThreshold) /
                                                  (GyroCorrectionMaxThreshold -
                                                   GyroCorrectionMinThreshold));
                correctionSpeed = gyroCorrectionLimit +
                    (correctionSpeed - gyroCorrectionLimit) * closeEnoughFactor;
            }

            Vector3 correction = gravityError / errorLength * correctionSpeed * deltaTime;
            gravity = correction.LengthSquared() < gravityError.LengthSquared()
                ? gravity + correction
                : targetGravity;
        }

        public void Map(Vector3 gyroDegPerSec, out float yawRate, out float pitchRate,
                        out float rollRadians) {
            // Normalize here as JoyShockMapper does. Fusion deliberately lets the gravity vector
            // converge smoothly, so its length is not guaranteed to remain exactly one; using
            // the unnormalized vector would make cursor gain vary during that convergence.
            Vector3 gravityDirection = gravity.LengthSquared() > 0.0f
                ? Vector3.Normalize(gravity)
                : new Vector3(0.0f, -1.0f, 0.0f);

            // Canonical GamepadMotionHelpers/JoyShockLibrary frame: Y is up, X is local pitch,
            // and the YZ plane contains local yaw and roll. Gravity chooses their horizontal
            // blend; it never contributes cursor velocity of its own.
            float worldYaw = gravityDirection.Y * gyroDegPerSec.Y +
                             gravityDirection.Z * gyroDegPerSec.Z;
            float yawPlaneSpeed = (float)Math.Sqrt(gyroDegPerSec.Y * gyroDegPerSec.Y +
                                                   gyroDegPerSec.Z * gyroDegPerSec.Z);
            float worldYawSign = worldYaw < 0.0f ? -1.0f : 1.0f;
            yawRate = worldYawSign * Math.Min(Math.Abs(worldYaw) * YawRelaxFactor,
                                              yawPlaneSpeed);
            pitchRate = gyroDegPerSec.X;

            // Diagnostic only: zero while the canonical controller frame is flat.
            rollRadians = (float)Math.Atan2(gravityDirection.X, -gravityDirection.Y);
        }

        private static Vector3 RotateByInverseLocalMotion(Vector3 value,
                                                           Vector3 rotationRadiansPerSecond,
                                                           float deltaTime) {
            float angularSpeed = rotationRadiansPerSecond.Length();
            if (angularSpeed <= 1e-8f || value.LengthSquared() <= 0.0f)
                return value;

            Vector3 axis = rotationRadiansPerSecond / angularSpeed;
            float angle = angularSpeed * deltaTime;
            float cos = (float)Math.Cos(angle);
            float sin = (float)Math.Sin(angle);
            return value * cos - Vector3.Cross(axis, value) * sin +
                   axis * Vector3.Dot(axis, value) * (1.0f - cos);
        }

        private static float Clamp01(float value) {
            return Math.Max(0.0f, Math.Min(1.0f, value));
        }
    }
}
