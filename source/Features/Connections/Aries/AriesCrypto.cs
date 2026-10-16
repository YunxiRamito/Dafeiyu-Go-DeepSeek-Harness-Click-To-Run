using System;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math.EC.Rfc8032;
using Org.BouncyCastle.Security;

namespace DeepSeekHarnessLauncher
{
    internal static partial class AriesCrypto
    {
        internal static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true
        };

        internal static string Encode(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        internal static byte[] Decode(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Contains('=') || value.Contains('+') || value.Contains('/'))
                throw new AriesServiceException("invalid_base64url", "Aries 数据编码无效。");
            return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
        }
        internal static string PublicKey(byte[] seed) => Encode(new Ed25519PrivateKeyParameters(seed, 0).GeneratePublicKey().GetEncoded());
        internal static string DeviceId(string publicKey) => "dev_" + Convert.ToHexString(SHA256.HashData(Decode(publicKey)).AsSpan(0, 16)).ToLowerInvariant();
        internal static string Fingerprint(string publicKey) => Convert.ToHexString(SHA256.HashData(CheckedPublicKey(publicKey)));
        internal static byte[] CheckedPublicKey(string publicKey)
        {
            byte[] pub = Decode(publicKey);
            if (pub.Length != 32 || !Ed25519.ValidatePublicKeyFull(pub, 0))
                throw new AriesServiceException("invalid_public_key", "设备 Ed25519 公钥无效。");
            return pub;
        }
        internal static void CheckIdentity(string deviceId, string publicKey)
        {
            CheckedPublicKey(publicKey);
            if (DeviceId(publicKey) != deviceId)
                throw new AriesServiceException("identity_mismatch", "设备 ID 与公钥不匹配。");
        }
        internal static void ConfirmFingerprint(string publicKey, string expected)
        {
            if (String.IsNullOrEmpty(expected) || !String.Equals(Fingerprint(publicKey), expected, StringComparison.OrdinalIgnoreCase))
                throw new AriesServiceException("fingerprint_confirmation_required", "必须显式确认完整设备公钥指纹。");
        }
        internal static string Sign(byte[] seed, string message)
        {
            var signer = new Ed25519Signer();
            signer.Init(true, new Ed25519PrivateKeyParameters(seed, 0));
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            signer.BlockUpdate(bytes, 0, bytes.Length);
            return Encode(signer.GenerateSignature());
        }
        private static void Verify(string publicKey, string message, string signature)
        {
            var signer = new Ed25519Signer();
            signer.Init(false, new Ed25519PublicKeyParameters(CheckedPublicKey(publicKey), 0));
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            signer.BlockUpdate(bytes, 0, bytes.Length);
            if (!signer.VerifySignature(Decode(signature)))
                throw new AriesServiceException("invalid_envelope_signature", "密钥信封签名校验失败。");
        }
        internal static string CanonicalRequest(string method, string pathAndQuery, string timestamp, string nonce, string idempotencyKey, byte[] body)
            => String.Join("\n", method, pathAndQuery, timestamp, nonce, idempotencyKey ?? String.Empty, Encode(SHA256.HashData(body)));

        // Edwards y -> Montgomery u = (1+y)/(1-y) mod (2^255-19).
        // Full subgroup validation rejects malformed and small-order Ed25519 points.
        internal static byte[] ToX25519Public(string publicKey)
        {
            byte[] point = CheckedPublicKey(publicKey);
            point[31] &= 0x7f;
            var p = (BigInteger.One << 255) - 19;
            var y = new BigInteger(point, true, false);
            var u = ((1 + y) * BigInteger.ModPow((1 - y + p) % p, p - 2, p)) % p;
            byte[] result = new byte[32];
            u.ToByteArray(true, false).CopyTo(result, 0);
            return result;
        }
        private static X25519PrivateKeyParameters ToX25519Private(byte[] seed)
        {
            byte[] hash = SHA512.HashData(seed);
            byte[] scalar = hash.AsSpan(0, 32).ToArray();
            scalar[0] &= 248; scalar[31] &= 127; scalar[31] |= 64;
            try { return new X25519PrivateKeyParameters(scalar, 0); }
            finally { CryptographicOperations.ZeroMemory(hash); CryptographicOperations.ZeroMemory(scalar); }
        }
        private static byte[] SharedSecret(X25519PrivateKeyParameters key, byte[] publicKey)
        {
            byte[] secret = new byte[32];
            try
            {
                key.GenerateSecret(new X25519PublicKeyParameters(publicKey, 0), secret, 0);
                if (CryptographicOperations.FixedTimeEquals(secret, new byte[32]))
                    throw new CryptographicException("Invalid X25519 agreement.");
                return secret;
            }
            catch { CryptographicOperations.ZeroMemory(secret); throw; }
        }
        private static byte[] WrappingKey(byte[] shared, string context)
        {
            byte[] salt = SHA256.HashData(Encoding.UTF8.GetBytes(context));
            byte[] prk = HMACSHA256.HashData(salt, shared);
            byte[] info = Encoding.UTF8.GetBytes("dafeiyu-aries-pair/v1/content-key\u0001");
            try { return HMACSHA256.HashData(prk, info); }
            finally { CryptographicOperations.ZeroMemory(prk); }
        }
        internal static string CreateEnvelope(byte[] seed, byte[] contentKey, string nodeId, string groupId,
            ulong epoch, string applicationId, string recipientId, string recipientPublicKey)
        {
            if (contentKey.Length != 32) throw new CryptographicException("Invalid content key.");
            CheckIdentity(recipientId, recipientPublicKey);
            var ephemeral = new X25519PrivateKeyParameters(new SecureRandom());
            var envelope = new AriesKeyEnvelope
            {
                NodeId = nodeId, GroupId = groupId, Epoch = epoch, ApplicationId = applicationId,
                SenderPublicKey = PublicKey(seed), RecipientId = recipientId, RecipientPublicKey = recipientPublicKey,
                EphemeralPublicKey = Encode(ephemeral.GeneratePublicKey().GetEncoded()), Nonce = Encode(RandomNumberGenerator.GetBytes(12))
            };
            envelope.SenderId = DeviceId(envelope.SenderPublicKey);
            string context = envelope.Context();
            byte[] shared = SharedSecret(ephemeral, ToX25519Public(recipientPublicKey));
            byte[] wrap = WrappingKey(shared, context);
            try
            {
                byte[] cipher = new byte[32], tag = new byte[16];
                using var aes = new AesGcm(wrap, 16);
                aes.Encrypt(Decode(envelope.Nonce), contentKey, cipher, tag, Encoding.UTF8.GetBytes(context));
                envelope.Ciphertext = Encode(cipher); envelope.Tag = Encode(tag);
                envelope.Signature = Sign(seed, context + "\n" + envelope.Ciphertext + "\n" + envelope.Tag);
                return Encode(JsonSerializer.SerializeToUtf8Bytes(envelope, Json));
            }
            finally { CryptographicOperations.ZeroMemory(shared); CryptographicOperations.ZeroMemory(wrap); }
        }
        internal static AriesKeyEnvelope ReadEnvelope(string encoded)
        {
            byte[] raw = Decode(encoded);
            if (raw.Length > 8192) throw new AriesServiceException("invalid_envelope", "密钥信封过大。");
            var e = JsonSerializer.Deserialize<AriesKeyEnvelope>(raw, Json)
                ?? throw new AriesServiceException("invalid_envelope", "密钥信封无效。");
            if (e.Protocol != "dafeiyu-aries-pair-key" || e.Version != 1 || e.Suite != "Ed25519-X25519-HKDF-SHA256-AES256GCM")
                throw new AriesServiceException("unsupported_envelope", "对方使用不兼容的配对密钥协议。");
            CheckIdentity(e.SenderId, e.SenderPublicKey);
            CheckIdentity(e.RecipientId, e.RecipientPublicKey);
            if (Decode(e.EphemeralPublicKey).Length != 32 || Decode(e.Nonce).Length != 12 ||
                Decode(e.Ciphertext).Length != 32 || Decode(e.Tag).Length != 16)
                throw new AriesServiceException("invalid_envelope", "密钥信封长度无效。");
            Verify(e.SenderPublicKey, e.Context() + "\n" + e.Ciphertext + "\n" + e.Tag, e.Signature);
            return e;
        }
        internal static byte[] OpenEnvelope(byte[] seed, AriesKeyEnvelope envelope)
        {
            if (PublicKey(seed) != envelope.RecipientPublicKey)
                throw new AriesServiceException("wrong_recipient", "密钥信封不属于本设备。");
            string context = envelope.Context();
            byte[] shared = SharedSecret(ToX25519Private(seed), Decode(envelope.EphemeralPublicKey));
            byte[] wrap = WrappingKey(shared, context), key = new byte[32];
            try
            {
                using var aes = new AesGcm(wrap, 16);
                aes.Decrypt(Decode(envelope.Nonce), Decode(envelope.Ciphertext), Decode(envelope.Tag), key, Encoding.UTF8.GetBytes(context));
                return key;
            }
            catch { CryptographicOperations.ZeroMemory(key); throw; }
            finally { CryptographicOperations.ZeroMemory(shared); CryptographicOperations.ZeroMemory(wrap); }
        }
    }

    internal sealed class AriesKeyEnvelope
    {
        public string Protocol { get; set; } = "dafeiyu-aries-pair-key";
        public int Version { get; set; } = 1;
        public string Suite { get; set; } = "Ed25519-X25519-HKDF-SHA256-AES256GCM";
        public string NodeId { get; set; }
        public string GroupId { get; set; }
        public ulong Epoch { get; set; }
        public string ApplicationId { get; set; }
        public string SenderId { get; set; }
        public string SenderPublicKey { get; set; }
        public string RecipientId { get; set; }
        public string RecipientPublicKey { get; set; }
        public string EphemeralPublicKey { get; set; }
        public string Nonce { get; set; }
        public string Ciphertext { get; set; }
        public string Tag { get; set; }
        public string Signature { get; set; }
        internal string Context() => String.Join("\n", Protocol, Version.ToString(CultureInfo.InvariantCulture), Suite,
            NodeId, GroupId, Epoch.ToString(CultureInfo.InvariantCulture), ApplicationId, SenderId, SenderPublicKey,
            RecipientId, RecipientPublicKey, EphemeralPublicKey, Nonce);
    }
}
