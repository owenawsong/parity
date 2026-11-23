using System.Buffers.Binary;
using System.Numerics;

namespace WPods.Core;

/// <summary>Recognizes a deliberate departure from neutral and return, during a short manual test.</summary>
internal sealed class HeadMotion(bool spatial = false)
{
    private const int GestureCalibrationSamples = 12;
    private int _count;
    private double _pitchNeutral;
    private double _yawNeutral;
    private double _pitch;
    private double _yaw;
    private double _roll;
    private string? _axis;
    private string? _lastGesture;
    private long _started;
    private long _cooldown;
    private bool _crossAxis;
    private int _direction;
    private bool _reversed;
    private int _stableSamples;
    private long _lastSample;
    private long _anchorAt;
    private long _firstOrientationAt;
    private bool _hasFusedVector;
    private bool _spatialReady;
    private int _gestures;
    private double _gestureScale = 1;
    private bool _awaitNeutral;
    private Quaternion _neutral;
    private Quaternion _attitude = Quaternion.Identity, _centerInverse = Quaternion.Identity;
    private Quaternion _latestOrientation;
    private (Quaternion Attitude, long At)? _suspectOrientation;
    private Vector3 _forward;
    private Vector3 _right;
    private static readonly byte[] ReportPrefix = [4, 0, 4, 0, 0x17, 0, 0, 0, 0x10, 0];

    internal int Samples => _count;
    internal string? Gesture => _lastGesture;
    internal bool Calibrated => spatial ? _spatialReady : _stableSamples >= GestureCalibrationSamples;
    internal byte? ReportService { get; private set; }
    internal double SpatialYaw => _yaw;
    internal double SpatialPitch => _pitch;
    // Positive bank means the right ear dips toward the shoulder.
    internal double SpatialRoll => _roll;
    internal int Gestures => _gestures;
    internal long LastSample => _lastSample;
    // Positive detector pitch means raising the chin relative to its starting position.
    internal int GesturePitchUp => (int)Math.Clamp(Math.Round(_pitch), -25, 25);
    internal int Yaw => (int)Math.Clamp(Math.Round(_yaw), -25, 25);

    // SensorDataWX. The protobuf sequence number can change the envelope size.
    internal static bool IsReport(ReadOnlySpan<byte> packet) =>
        packet.Length > 60 &&
        packet[..10].SequenceEqual(ReportPrefix) &&
        (packet[10] is 0x44 or 0x45 && packet[11] == 0 || !MotionBlock(packet, out _).IsEmpty);

    private static ReadOnlySpan<byte> MotionBlock(ReadOnlySpan<byte> packet, out byte service)
    {
        service = 0;
        // The two services requested below return field 7 { 1: service, 3: bytes[58] }.
        // Locate this trailing field relative to the frame end, not to the sequence varint.
        const int blockSize = 58;
        if (packet.Length < 12 + 6 + blockSize || BinaryPrimitives.ReadUInt16LittleEndian(packet[10..]) != packet.Length - 12)
            return default;
        var field = packet[^(6 + blockSize)..];
        if (field[0] != 0x3a || field[1] != 62 || field[2] != 8 || field[3] is not (14 or 16) ||
            field[4] != 0x1a || field[5] != blockSize || field[6] != 1) return default;
        service = field[3];
        return field[6..];
    }

    internal static bool UseAlternateFirst(byte? verifiedService, bool pro3) =>
        verifiedService is 14 or 16 ? verifiedService == 16 : pro3;

    internal static byte[] Command(bool start, bool alternate) => alternate
        ? [4, 0, 4, 0, 0x17, 0, 0, 0, 0x10, 0, 0x0F, 0, 8, start ? (byte)0x73 : (byte)0x75,
            0x42, 0x0B, 8, 0x10, 0x10, 2, 0x1A, 5, 1, start ? (byte)0x40 : (byte)0, start ? (byte)0x9C : (byte)0, 0, 0]
        : start
            ? [4, 0, 4, 0, 0x17, 0, 0, 0, 0x10, 0, 0x10, 0, 8, 0xA1, 2, 0x42, 0x0B, 8, 0x0E, 0x10, 2, 0x1A, 5, 1, 0x40, 0x9C, 0, 0]
            : [4, 0, 4, 0, 0x17, 0, 0, 0, 0x10, 0, 0x11, 0, 8, 0x7E, 0x10, 2, 0x42, 0x0B, 8, 0x4E, 0x10, 2, 0x1A, 5, 1, 0, 0, 0, 0];

    internal bool Observe(ReadOnlySpan<byte> packet, long now)
    {
        if (!IsReport(packet)) return false;
        var block = MotionBlock(packet, out byte service);
        if (spatial)
        {
            if (block.IsEmpty) return false;
            const float q15 = 1f / 32768;
            var vector = new Vector3(BinaryPrimitives.ReadInt16LittleEndian(block[20..]),
                BinaryPrimitives.ReadInt16LittleEndian(block[22..]), BinaryPrimitives.ReadInt16LittleEndian(block[24..])) * q15;
            float lengthSquared = vector.LengthSquared();
            if (lengthSquared > 1.01f) return false;
            var attitude = Quaternion.Normalize(new Quaternion(vector, MathF.Sqrt(MathF.Max(0, 1 - lengthSquared))));
            ReportService = service;
            ObserveOrientation(attitude, now);
            return true;
        }

        // Keep the established gesture sensitivities and their legacy packet path.
        var axes = block.IsEmpty ? packet[45..] : block[22..];
        if (!block.IsEmpty) ReportService = service;
        int y = BinaryPrimitives.ReadInt16LittleEndian(axes);
        int z = BinaryPrimitives.ReadInt16LittleEndian(axes[2..]);
        const double scale = 90d / 32_000;
        ObserveAxes((y + z) * scale, (y - z) * scale, now);
        return true;
    }

    private void ObserveOrientation(Quaternion attitude, long now)
    {
        long elapsed = now - _lastSample;
        if (_spatialReady && _hasFusedVector && elapsed >= 0 && !Coherent(_latestOrientation, attitude, elapsed))
        {
            // A single corrupted or reset pose must not pull sound across the
            // listener. Accept a new nonzero orientation once another frame
            // corroborates it. A zero burst does not prove a sensor reset.
            if (attitude == Quaternion.Identity) return;
            if (elapsed <= 160)
            {
                var previous = _suspectOrientation;
                if (previous is not { } sample || now - sample.At is < 8 or > 160 ||
                    !Coherent(sample.Attitude, attitude, now - sample.At))
                {
                    if (previous is null || now - previous.Value.At >= 8)
                        _suspectOrientation = (attitude, now);
                    return;
                }
            }
        }
        _suspectOrientation = null;
        _lastSample = now;
        _latestOrientation = attitude;
        _count++;
        if (_count == 1) _firstOrientationAt = now;
        if (!_hasFusedVector)
        {
            // The sensor can stream exact zeros while its orientation estimate starts.
            // An identity pose is also physically valid, so accept a sustained identity,
            // but check its first departure before using that departure to rotate audio.
            if (attitude == Quaternion.Identity)
            {
                if (now - _firstOrientationAt < 200)
                {
                    _neutral = attitude;
                    _anchorAt = now;
                    return;
                }
            }
            else
            {
                _hasFusedVector = true;
                if (!_spatialReady || !Coherent(Quaternion.Identity, attitude, elapsed))
                {
                    _neutral = attitude;
                    _anchorAt = now;
                    _spatialReady = false;
                    return;
                }
            }
        }
        if (!Calibrated)
        {
            // The packet contains a fused attitude. Two coherent reports establish a
            // fresh forward direction even if the listener is already turning.
            long separation = now - _anchorAt;
            if (_count == 1 || separation < 0 || separation > 160)
            {
                _neutral = attitude;
                _anchorAt = now;
                return;
            }
            if (separation < 8) return; // Several frames can arrive in one Bluetooth delivery.
            bool coherent = Coherent(_neutral, attitude, separation);
            _neutral = attitude;
            _anchorAt = now;
            if (!coherent) return;

            _spatialReady = true;
            // Fused attitude maps the earbud into a world frame with Z up. Estimate the
            // horizontal right axis from the existing chin-up gesture (increasing qY + qZ)
            // at neutral. Pure horizontal turns are independent of this mounting estimate.
            var vector = new Vector3(_neutral.X, _neutral.Y, _neutral.Z);
            var chinUp = new Vector3(0, 1, 1);
            var right = _neutral.W * chinUp + Vector3.Cross(vector, chinUp);
            right.Z = 0;
            _right = right.LengthSquared() > .01f ? Vector3.Normalize(right) : Vector3.UnitX;
            _forward = Vector3.Cross(Vector3.UnitZ, _right);
            return;
        }

        _attitude = attitude * Quaternion.Conjugate(_neutral);
        Quaternion relative = _centerInverse * _attitude;
        Vector3 facing = Vector3.Transform(_forward, relative);
        Vector3 earRight = Vector3.Transform(_right, relative);
        double horizontal = Math.Sqrt(facing.X * facing.X + facing.Y * facing.Y);
        // Directly overhead the azimuth is undefined. Hold the previous one there.
        double yaw = horizontal < .035 ? _yaw :
            Math.Atan2(Vector3.Dot(facing, _right), Vector3.Dot(facing, _forward)) * (180 / Math.PI);
        // Resolve the ear-to-ear axis around the gaze direction. At the pole this
        // retains the last heading and obtains the remaining rotation from the ears.
        var (sin, cos) = MathF.SinCos((float)(yaw * (Math.PI / 180)));
        double pitch = Math.Atan2(facing.Z, Vector3.Dot(facing, sin * _right + cos * _forward)) * (180 / Math.PI);
        Vector3 levelUp = Vector3.Normalize(Vector3.Cross(cos * _right - sin * _forward, facing));
        Vector3 levelRight = Vector3.Cross(facing, levelUp);
        double roll = Math.Atan2(-Vector3.Dot(earRight, levelUp), Vector3.Dot(earRight, levelRight)) * (180 / Math.PI);
        // A rotation has two equivalent Euler descriptions. Following the nearer
        // one prevents a 180° yaw-and-bank flip when looking through the zenith.
        double turn = Math.IEEERemainder(yaw - _yaw, 360);
        double tilt = Math.IEEERemainder(pitch - _pitch, 360);
        double bank = Math.IEEERemainder(roll - _roll, 360);
        double altTurn = Math.IEEERemainder(turn + 180, 360);
        double altTilt = Math.IEEERemainder(180 - pitch - _pitch, 360);
        double altBank = Math.IEEERemainder(bank + 180, 360);
        if (altTurn * altTurn + altTilt * altTilt + altBank * altBank < turn * turn + tilt * tilt + bank * bank)
            (turn, tilt, bank) = (altTurn, altTilt, altBank);
        double alpha = 1 - Math.Exp(-Math.Clamp(elapsed, 1L, 100L) / 24d);
        _yaw += alpha * turn;
        _pitch += alpha * tilt;
        _roll += alpha * bank;
    }

    private static bool Coherent(Quaternion from, Quaternion to, long intervalMs)
    {
        double maxTurnDegrees = Math.Min(60, 2 + .72 * Math.Max(0, intervalMs));
        return Math.Abs(Quaternion.Dot(from, to)) >= Math.Cos(maxTurnDegrees * Math.PI / 360);
    }

    internal void RecenterSpatial()
    {
        if (!spatial || !Calibrated) return;
        _centerInverse = Quaternion.Conjugate(_attitude);
        _yaw = _pitch = _roll = 0;
    }

    internal void SetGestureSensitivity(string sensitivity)
    {
        double scale = sensitivity switch { "high" => .75, "low" => 1.4, _ => 1 };
        if (spatial || scale == _gestureScale) return;
        _gestureScale = scale;
        // Never finish a gesture begun under a previous threshold.
        _axis = null;
        _awaitNeutral = Calibrated;
    }

    internal void ObserveAxes(double pitch, double yaw, long now)
    {
        _count++;
        if (now - _lastSample > 400)
        {
            _stableSamples = 0;
            _axis = null;
            _pitch = _yaw = 0;
        }
        _lastSample = now;
        if (!Calibrated)
        {
            if (_stableSamples == 0 || Math.Abs(pitch - _pitchNeutral) > 4 || Math.Abs(yaw - _yawNeutral) > 4)
            {
                _stableSamples = 0;
                _pitchNeutral = pitch;
                _yawNeutral = yaw;
            }
            _stableSamples++;
            _pitchNeutral += (pitch - _pitchNeutral) / _stableSamples;
            _yawNeutral += (yaw - _yawNeutral) / _stableSamples;
            return;
        }

        _pitch = .5 * _pitch + .5 * (pitch - _pitchNeutral);
        _yaw = .5 * _yaw + .5 * (yaw - _yawNeutral);
        double vertical = Math.Abs(_pitch);
        double horizontal = Math.Abs(_yaw);
        bool neutral = vertical < 3.5 && horizontal < 4;
        if (_awaitNeutral)
        {
            _awaitNeutral = !neutral;
            return;
        }
        if (_axis is null)
        {
            if (now < _cooldown) return;
            if (vertical >= 7 * _gestureScale && vertical >= horizontal * 1.65) _axis = "nod";
            else if (horizontal >= 9 * _gestureScale && horizontal >= vertical * 1.65) _axis = "shake";
            else
            {
                if (neutral)
                {
                    _pitchNeutral += (pitch - _pitchNeutral) * .025;
                    _yawNeutral += (yaw - _yawNeutral) * .025;
                }
                return;
            }
            _started = now;
            _crossAxis = false;
            _direction = Math.Sign(_axis == "nod" ? _pitch : _yaw);
            _reversed = false;
            return;
        }
        _crossAxis |= _axis == "nod" ? horizontal > 8 : vertical > 8;
        if (_axis == "shake" && _yaw * _direction < -6 * _gestureScale) _reversed = true;
        long elapsed = now - _started;
        bool returned = neutral && (_axis == "nod" || _reversed);
        if (returned || elapsed > 1150)
        {
            if (returned && elapsed is >= 95 and <= 1150 && !_crossAxis)
            {
                _lastGesture = _axis;
                _gestures++;
            }
            _axis = null;
            _cooldown = now + 450;
        }
    }
}
