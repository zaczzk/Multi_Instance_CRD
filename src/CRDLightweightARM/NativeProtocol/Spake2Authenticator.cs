using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CRDLightweightARM.NativeProtocol;

/// <summary>
/// Implements Chromoting's exact SPAKE2 authentication protocol and cryptographic primitives
/// as defined in Chromium's remoting/protocol/spake2_authenticator.cc and BoringSSL spake25519.
/// Provides both pure managed Ed25519/Curve25519 scalar-point mathematics and a P/Invoke interface
/// for BoringSSL SPAKE2_CTX.
/// </summary>
public static class Spake2Authenticator
{
    #region Chromoting Key Derivation & Verification Primitives

    /// <summary>
    /// Derives the PIN-based shared secret used for SPAKE2 authentication in Chromoting:
    /// HMAC-SHA256(ASCII(hostId), ASCII(pin)).
    /// </summary>
    public static byte[] DerivePinSharedSecret(string hostId, string pin)
    {
        ArgumentNullException.ThrowIfNull(hostId);
        ArgumentNullException.ThrowIfNull(pin);

        byte[] key = Encoding.ASCII.GetBytes(hostId);
        byte[] data = Encoding.ASCII.GetBytes(pin);
        return HMACSHA256.HashData(key, data);
    }

    /// <summary>
    /// Derives the PIN-based shared secret using spans for zero-allocation performance.
    /// </summary>
    public static void DerivePinSharedSecret(ReadOnlySpan<char> hostId, ReadOnlySpan<char> pin, Span<byte> destination)
    {
        Span<byte> keyBytes = stackalloc byte[Encoding.ASCII.GetByteCount(hostId)];
        Encoding.ASCII.GetBytes(hostId, keyBytes);

        Span<byte> pinBytes = stackalloc byte[Encoding.ASCII.GetByteCount(pin)];
        Encoding.ASCII.GetBytes(pin, pinBytes);

        HMACSHA256.HashData(keyBytes, pinBytes, destination);
    }

    /// <summary>
    /// Writes a 64-bit little-endian length prefix (LP64) followed by raw byte data into a buffer.
    /// </summary>
    public static void AppendLP64(List<byte> buffer, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(len, (ulong)data.Length);
        buffer.AddRange(len);
        buffer.AddRange(data);
    }

    /// <summary>
    /// Builds the exact SPAKE2 transcript with 64-bit little-endian length prefixes (LP64)
    /// conforming to BoringSSL spake25519 and RFC 9383:
    /// LP64(Alice) || LP64(Bob) || LP64(X) || LP64(Y) || LP64(K) || LP64(w).
    /// </summary>
    public static byte[] BuildSpake2Transcript(
        ReadOnlySpan<byte> aliceId,
        ReadOnlySpan<byte> bobId,
        ReadOnlySpan<byte> msgAlice,
        ReadOnlySpan<byte> msgBob,
        ReadOnlySpan<byte> sharedKeyPoint,
        ReadOnlySpan<byte> sharedSecret)
    {
        int totalSize = 8 * 6 + aliceId.Length + bobId.Length + msgAlice.Length + msgBob.Length + sharedKeyPoint.Length + sharedSecret.Length;
        var buffer = new List<byte>(totalSize);

        AppendLP64(buffer, aliceId);
        AppendLP64(buffer, bobId);
        AppendLP64(buffer, msgAlice);
        AppendLP64(buffer, msgBob);
        AppendLP64(buffer, sharedKeyPoint);
        AppendLP64(buffer, sharedSecret);

        return buffer.ToArray();
    }

    /// <summary>
    /// Builds a generic SPAKE2 transcript by appending each element with a 64-bit little-endian length prefix (LP64).
    /// </summary>
    public static byte[] BuildSpake2TranscriptFromElements(params ReadOnlyMemory<byte>[] elements)
    {
        int estimatedSize = elements.Length * 8;
        foreach (var el in elements) estimatedSize += el.Length;

        var buffer = new List<byte>(estimatedSize);
        foreach (var el in elements)
        {
            AppendLP64(buffer, el.Span);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Calculates Chromoting's exact verification hash (remoting/protocol/spake2_authenticator.cc):
    /// HMAC-SHA256(authKey, ("host"|"client") || LP32_BE(localJid) || LP32_BE(remoteJid) [|| LP32_BE(hostCertDer)]).
    /// </summary>
    public static byte[] CalculateVerificationHash(
        byte[] authKey,
        bool fromHost,
        string localJid,
        string remoteJid,
        byte[]? hostCertDer = null)
    {
        ArgumentNullException.ThrowIfNull(authKey);
        ArgumentNullException.ThrowIfNull(localJid);
        ArgumentNullException.ThrowIfNull(remoteJid);

        ReadOnlySpan<byte> direction = fromHost ? "host"u8 : "client"u8;
        byte[] localJidBytes = Encoding.UTF8.GetBytes(localJid);
        byte[] remoteJidBytes = Encoding.UTF8.GetBytes(remoteJid);

        int totalLen = direction.Length + 4 + localJidBytes.Length + 4 + remoteJidBytes.Length;
        if (hostCertDer is not null && hostCertDer.Length > 0)
        {
            totalLen += 4 + hostCertDer.Length;
        }

        byte[] payload = new byte[totalLen];
        int offset = 0;

        // Direction string ("host" or "client")
        direction.CopyTo(payload.AsSpan(offset));
        offset += direction.Length;

        // LP32_BE(localJid)
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset, 4), (uint)localJidBytes.Length);
        offset += 4;
        localJidBytes.CopyTo(payload.AsSpan(offset));
        offset += localJidBytes.Length;

        // LP32_BE(remoteJid)
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset, 4), (uint)remoteJidBytes.Length);
        offset += 4;
        remoteJidBytes.CopyTo(payload.AsSpan(offset));
        offset += remoteJidBytes.Length;

        // LP32_BE(hostCertDer) if provided
        if (hostCertDer is not null && hostCertDer.Length > 0)
        {
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset, 4), (uint)hostCertDer.Length);
            offset += 4;
            hostCertDer.CopyTo(payload.AsSpan(offset));
            offset += hostCertDer.Length;
        }

        return HMACSHA256.HashData(authKey, payload);
    }

    #endregion

    #region Pure Managed Ed25519 / Curve25519 Arithmetic

    /// <summary>
    /// Pure managed constant-time finite field arithmetic for Curve25519 / Ed25519 (GF(2^255 - 19)).
    /// </summary>
    public static class Ed25519
    {
        // p = 2^255 - 19
        public static readonly BigInteger P = (BigInteger.One << 255) - 19;

        // Order of the base point l = 2^252 + 27742317777372353535851937790883648493
        public static readonly BigInteger L = (BigInteger.One << 252) + BigInteger.Parse("27742317777372353535851937790883648493");

        // Edwards curve constant d = -121665 * inv(121666) mod p
        public static readonly BigInteger D = Mod(
            -121665 * ModInverse(121666, P),
            P
        );

        // I = sqrt(-1) mod p = 2^((p-1)/4) mod p
        public static readonly BigInteger I = BigInteger.ModPow(2, (P - 1) / 4, P);

        // Standard Ed25519 base point G (x, 4/5 mod p)
        public static readonly Point BasePointG;

        // BoringSSL SPAKE2 Point M (Hex: 5ada7e4bf6ddd9adb6626d32131c6b5c51a1e347a3478f53cfcf441b88eed12e)
        public static readonly Point PointM;

        // BoringSSL SPAKE2 Point N (Hex: 10e3df0ae37d8e7a99b5fe74b44672103dbddcbd06af680d71329a11693bc778)
        public static readonly Point PointN;

        static Ed25519()
        {
            // Compute BasePoint G
            BigInteger gy = Mod(4 * ModInverse(5, P), P);
            BigInteger gx = RecoverX(gy, 0);
            BasePointG = new Point(gx, gy, BigInteger.One, Mod(gx * gy, P));

            // Decode Point M
            byte[] mBytes = Convert.FromHexString("5ada7e4bf6ddd9adb6626d32131c6b5c51a1e347a3478f53cfcf441b88eed12e");
            PointM = DecodePoint(mBytes);

            // Decode Point N
            byte[] nBytes = Convert.FromHexString("10e3df0ae37d8e7a99b5fe74b44672103dbddcbd06af680d71329a11693bc778");
            PointN = DecodePoint(nBytes);
        }

        public static BigInteger Mod(BigInteger a, BigInteger m)
        {
            BigInteger r = a % m;
            return r < 0 ? r + m : r;
        }

        public static BigInteger ModInverse(BigInteger a, BigInteger m)
        {
            // By Fermat's little theorem for prime p: a^(p-2) mod p
            return BigInteger.ModPow(Mod(a, m), m - 2, m);
        }

        /// <summary>
        /// Recovers the X coordinate on the twisted Edwards curve given Y and the sign bit of X.
        /// x^2 = (y^2 - 1) / (d*y^2 + 1) mod p.
        /// </summary>
        public static BigInteger RecoverX(BigInteger y, int signBit)
        {
            BigInteger u = Mod(y * y - 1, P);
            BigInteger v = Mod(D * y * y + 1, P);
            BigInteger vInv = ModInverse(v, P);
            BigInteger x2 = Mod(u * vInv, P);

            // Square root mod p = x2^((p+3)/8) mod p
            BigInteger x = BigInteger.ModPow(x2, (P + 3) / 8, P);
            if (Mod(x * x - x2, P) != 0)
            {
                x = Mod(x * I, P);
            }
            if (Mod(x * x - x2, P) != 0)
            {
                throw new CryptographicException("Failed to decode Ed25519 curve point: not on curve.");
            }

            if ((x % 2 != 0 ? 1 : 0) != signBit)
            {
                x = P - x;
            }
            return x;
        }

        /// <summary>
        /// Decodes a 32-byte Ed25519 point representation into extended coordinates.
        /// </summary>
        public static Point DecodePoint(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != 32)
                throw new ArgumentException("Ed25519 point must be exactly 32 bytes.", nameof(bytes));

            byte[] copy = bytes.ToArray();
            int signBit = (copy[31] >> 7) & 1;
            copy[31] &= 0x7F; // Clear sign bit

            BigInteger y = new BigInteger(copy, isUnsigned: true, isBigEndian: false);
            if (y >= P)
                throw new CryptographicException("Invalid Y coordinate: >= field prime.");

            BigInteger x = RecoverX(y, signBit);
            return new Point(x, y, BigInteger.One, Mod(x * y, P));
        }

        /// <summary>
        /// Encodes an Ed25519 point in extended coordinates into a 32-byte compressed format.
        /// </summary>
        public static byte[] EncodePoint(Point pt)
        {
            BigInteger zInv = ModInverse(pt.Z, P);
            BigInteger x = Mod(pt.X * zInv, P);
            BigInteger y = Mod(pt.Y * zInv, P);

            byte[] result = new byte[32];
            byte[] yBytes = y.ToByteArray(isUnsigned: true, isBigEndian: false);
            Array.Copy(yBytes, result, Math.Min(yBytes.Length, 32));

            // Set MSB to sign bit of X (x % 2)
            if (x % 2 != 0)
            {
                result[31] |= 0x80;
            }
            return result;
        }

        /// <summary>
        /// Extended homogeneous coordinates (X : Y : Z : T) where x = X/Z, y = Y/Z, xy = T/Z.
        /// </summary>
        public readonly struct Point
        {
            public readonly BigInteger X;
            public readonly BigInteger Y;
            public readonly BigInteger Z;
            public readonly BigInteger T;

            public Point(BigInteger x, BigInteger y, BigInteger z, BigInteger t)
            {
                X = x;
                Y = y;
                Z = z;
                T = t;
            }

            public static Point Identity => new(BigInteger.Zero, BigInteger.One, BigInteger.One, BigInteger.Zero);

            public static Point operator +(Point p1, Point p2)
            {
                // Extended coordinate addition
                BigInteger a = Mod((p1.Y - p1.X) * (p2.Y - p2.X), P);
                BigInteger b = Mod((p1.Y + p1.X) * (p2.Y + p2.X), P);
                BigInteger c = Mod(2 * p1.T * p2.T * D, P);
                BigInteger d = Mod(2 * p1.Z * p2.Z, P);
                BigInteger e = Mod(b - a, P);
                BigInteger f = Mod(d - c, P);
                BigInteger g = Mod(d + c, P);
                BigInteger h = Mod(b + a, P);

                BigInteger x3 = Mod(e * f, P);
                BigInteger y3 = Mod(g * h, P);
                BigInteger t3 = Mod(e * h, P);
                BigInteger z3 = Mod(f * g, P);

                return new Point(x3, y3, z3, t3);
            }

            public static Point operator -(Point p1, Point p2)
            {
                // Invert P2: (X, Y, Z, T) -> (-X, Y, Z, -T)
                Point negP2 = new(Mod(P - p2.X, P), p2.Y, p2.Z, Mod(P - p2.T, P));
                return p1 + negP2;
            }

            public Point Double()
            {
                BigInteger a = Mod(X * X, P);
                BigInteger b = Mod(Y * Y, P);
                BigInteger c = Mod(2 * Z * Z, P);
                BigInteger h = Mod(a + b, P);
                BigInteger e = Mod(h - Mod((X + Y) * (X + Y), P), P);
                BigInteger g = Mod(a - b, P);
                BigInteger f = Mod(c + g, P);

                BigInteger x3 = Mod(e * f, P);
                BigInteger y3 = Mod(g * h, P);
                BigInteger t3 = Mod(e * h, P);
                BigInteger z3 = Mod(f * g, P);

                return new Point(x3, y3, z3, t3);
            }

            public static Point Multiply(Point pt, BigInteger scalar)
            {
                scalar = Mod(scalar, L);
                Point result = Identity;
                Point addend = pt;

                while (scalar > 0)
                {
                    if ((scalar & 1) == 1)
                    {
                        result = result + addend;
                    }
                    addend = addend.Double();
                    scalar >>= 1;
                }
                return result;
            }
        }
    }

    #endregion

    #region High-Level SPAKE2 Session Implementation

    /// <summary>
    /// Pure managed SPAKE2 protocol session orchestrator for client or host in Chromoting.
    /// Can execute completely without native dependencies.
    /// </summary>
    public sealed class Session
    {
        public enum SessionRole
        {
            Alice, // Client
            Bob    // Host
        }

        public enum SessionState
        {
            Initialized,
            MessageGenerated,
            KeyEstablished,
            Verified,
            Failed
        }

        private readonly SessionRole _role;
        private readonly byte[] _localId;
        private readonly byte[] _remoteId;
        private readonly byte[] _sharedSecret;
        private readonly BigInteger _privateScalar;
        private readonly BigInteger _passwordScalar;
        private byte[]? _localMessage;
        private byte[]? _authKey;
        private byte[]? _outgoingVerificationHash;
        private byte[]? _expectedPeerVerificationHash;
        private SessionState _state;

        public SessionRole Role => _role;
        public SessionState State => _state;
        public byte[]? AuthKey => _authKey;

        public Session(SessionRole role, string localId, string remoteId, byte[] sharedSecret)
        {
            ArgumentNullException.ThrowIfNull(localId);
            ArgumentNullException.ThrowIfNull(remoteId);
            ArgumentNullException.ThrowIfNull(sharedSecret);

            _role = role;
            _localId = Encoding.UTF8.GetBytes(localId);
            _remoteId = Encoding.UTF8.GetBytes(remoteId);
            _sharedSecret = sharedSecret;

            // Generate random 256-bit private scalar x in [1, L-1]
            byte[] rand = new byte[32];
            RandomNumberGenerator.Fill(rand);
            _privateScalar = Ed25519.Mod(new BigInteger(rand, isUnsigned: true, isBigEndian: false), Ed25519.L - 1) + 1;

            // Derive password scalar w from shared secret: SHA512(sharedSecret) mod L
            byte[] hash = SHA512.HashData(_sharedSecret);
            _passwordScalar = Ed25519.Mod(new BigInteger(hash, isUnsigned: true, isBigEndian: false), Ed25519.L);

            _state = SessionState.Initialized;
        }

        /// <summary>
        /// Generates the public SPAKE2 message to send to the peer:
        /// For Alice (client): X = x*G + w*M
        /// For Bob (host):     Y = y*G + w*N
        /// </summary>
        public byte[] GenerateMessage()
        {
            if (_localMessage is not null)
                return _localMessage;

            Ed25519.Point term1 = Ed25519.Point.Multiply(Ed25519.BasePointG, _privateScalar);
            Ed25519.Point term2 = _role == SessionRole.Alice
                ? Ed25519.Point.Multiply(Ed25519.PointM, _passwordScalar)
                : Ed25519.Point.Multiply(Ed25519.PointN, _passwordScalar);

            Ed25519.Point messagePoint = term1 + term2;
            _localMessage = Ed25519.EncodePoint(messagePoint);
            _state = SessionState.MessageGenerated;
            return _localMessage;
        }

        /// <summary>
        /// Processes the peer's public message and establishes the shared authentication key (auth_key):
        /// For Alice: K = 8 * x * (Y - w*N)
        /// For Bob:   K = 8 * y * (X - w*M)
        /// </summary>
        public void ProcessPeerMessage(ReadOnlySpan<byte> peerMessage, byte[]? hostCertDer = null)
        {
            if (_state != SessionState.MessageGenerated)
                throw new InvalidOperationException("GenerateMessage() must be called before ProcessPeerMessage().");

            Ed25519.Point peerPoint = Ed25519.DecodePoint(peerMessage);

            // Subtract blinding term
            Ed25519.Point blinding = _role == SessionRole.Alice
                ? Ed25519.Point.Multiply(Ed25519.PointN, _passwordScalar)
                : Ed25519.Point.Multiply(Ed25519.PointM, _passwordScalar);

            Ed25519.Point unblinded = peerPoint - blinding;

            // Multiply by cofactor 8 and private scalar
            BigInteger cofactorScalar = Ed25519.Mod(8 * _privateScalar, Ed25519.L);
            Ed25519.Point sharedPoint = Ed25519.Point.Multiply(unblinded, cofactorScalar);
            byte[] kBytes = Ed25519.EncodePoint(sharedPoint);

            byte[] aliceId = _role == SessionRole.Alice ? _localId : _remoteId;
            byte[] bobId = _role == SessionRole.Alice ? _remoteId : _localId;
            byte[] msgAlice = _role == SessionRole.Alice ? _localMessage! : peerMessage.ToArray();
            byte[] msgBob = _role == SessionRole.Alice ? peerMessage.ToArray() : _localMessage!;

            // Build transcript: LP64(Alice) || LP64(Bob) || LP64(X) || LP64(Y) || LP64(K) || LP64(w)
            byte[] wBytes = new byte[32];
            byte[] wRaw = _passwordScalar.ToByteArray(isUnsigned: true, isBigEndian: false);
            Array.Copy(wRaw, wBytes, Math.Min(wRaw.Length, 32));

            byte[] transcript = BuildSpake2Transcript(aliceId, bobId, msgAlice, msgBob, kBytes, wBytes);

            // auth_key is SHA512 of transcript in BoringSSL SPAKE2
            byte[] fullKey = SHA512.HashData(transcript);
            _authKey = new byte[32];
            Array.Copy(fullKey, 0, _authKey, 0, 32);

            string localJid = Encoding.UTF8.GetString(_localId);
            string remoteJid = Encoding.UTF8.GetString(_remoteId);

            bool isHost = _role == SessionRole.Bob;
            _outgoingVerificationHash = CalculateVerificationHash(_authKey, isHost, localJid, remoteJid, hostCertDer);
            _expectedPeerVerificationHash = CalculateVerificationHash(_authKey, !isHost, remoteJid, localJid, hostCertDer);

            _state = SessionState.KeyEstablished;
        }

        /// <summary>
        /// Returns the verification hash to transmit to the peer.
        /// </summary>
        public byte[] GetOutgoingVerificationHash()
        {
            if (_outgoingVerificationHash is null)
                throw new InvalidOperationException("Key not established yet. Call ProcessPeerMessage() first.");
            return _outgoingVerificationHash;
        }

        /// <summary>
        /// Verifies the verification hash received from the peer.
        /// </summary>
        public bool VerifyPeerHash(ReadOnlySpan<byte> peerHash)
        {
            if (_expectedPeerVerificationHash is null)
                throw new InvalidOperationException("Key not established yet. Call ProcessPeerMessage() first.");

            bool match = CryptographicOperations.FixedTimeEquals(peerHash, _expectedPeerVerificationHash);
            _state = match ? SessionState.Verified : SessionState.Failed;
            return match;
        }
    }

    #endregion

    #region BoringSSL P/Invoke Native Interface

    /// <summary>
    /// Safe P/Invoke wrapper for BoringSSL's native SPAKE2 implementation (e.g. from chrome.dll or libcrypto).
    /// </summary>
    public static class BoringSslNative
    {
        public enum Spake2Role
        {
            Alice = 0,
            Bob = 1
        }

        private const string BoringSslLibrary = "chrome.dll";

        [DllImport(BoringSslLibrary, EntryPoint = "SPAKE2_CTX_new", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SPAKE2_CTX_new(
            Spake2Role role,
            [In] byte[] myName,
            UIntPtr myNameLen,
            [In] byte[] theirName,
            UIntPtr theirNameLen);

        [DllImport(BoringSslLibrary, EntryPoint = "SPAKE2_CTX_free", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SPAKE2_CTX_free(IntPtr ctx);

        [DllImport(BoringSslLibrary, EntryPoint = "SPAKE2_generate_msg", CallingConvention = CallingConvention.Cdecl)]
        public static extern int SPAKE2_generate_msg(
            IntPtr ctx,
            [Out] byte[] outMsg,
            out UIntPtr outMsgLen,
            UIntPtr maxOutMsgLen,
            [In] byte[] password,
            UIntPtr passwordLen);

        [DllImport(BoringSslLibrary, EntryPoint = "SPAKE2_process_msg", CallingConvention = CallingConvention.Cdecl)]
        public static extern int SPAKE2_process_msg(
            IntPtr ctx,
            [Out] byte[] outKey,
            out UIntPtr outKeyLen,
            UIntPtr maxOutKeyLen,
            [In] byte[] theirMsg,
            UIntPtr theirMsgLen);

        /// <summary>
        /// Tests whether the native BoringSSL export is available in current process address space.
        /// </summary>
        public static bool IsNativeAvailable()
        {
            try
            {
                IntPtr hModule = NativeLibrary.Load(BoringSslLibrary);
                if (hModule != IntPtr.Zero)
                {
                    bool hasExport = NativeLibrary.TryGetExport(hModule, "SPAKE2_CTX_new", out _);
                    NativeLibrary.Free(hModule);
                    return hasExport;
                }
            }
            catch
            {
                // Fall back to pure managed
            }
            return false;
        }
    }

    #endregion
}
