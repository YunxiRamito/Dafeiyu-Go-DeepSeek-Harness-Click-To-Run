using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeepSeekHarnessLauncher
{
    internal sealed class AriesProviderException : Exception
    {
        public string Code { get; }
        public bool Retryable { get; }
        public AriesProviderException(string code, string message, bool retryable = false)
            : base(Bound(message, 240))
        { Code = Bound(code, 64); Retryable = retryable; }
        private static string Bound(string value, int limit)
        {
            value ??= String.Empty;
            var text = new StringBuilder(Math.Min(value.Length, limit));
            foreach (char c in value)
            { if (!Char.IsControl(c)) text.Append(c); if (text.Length == limit) break; }
            return text.ToString();
        }
    }

    internal sealed class AriesContentEnvelope
    {
        public string Protocol { get; set; } = "dafeiyu-aries-content";
        public int Version { get; set; } = 1;
        public string Type { get; set; } = String.Empty;
        public string NodeId { get; set; } = String.Empty;
        public string GroupId { get; set; } = String.Empty;
        public ulong Epoch { get; set; }
        public string SenderId { get; set; } = String.Empty;
        public string ObjectId { get; set; } = String.Empty;
        public string Nonce { get; set; } = String.Empty;
        public string Ciphertext { get; set; } = String.Empty;
        public string Tag { get; set; } = String.Empty;
        public string Signature { get; set; } = String.Empty;
    }

    internal sealed class AriesFileManifest
    {
        public string RelativePath { get; set; } = String.Empty;
        public long Size { get; set; }
        public int ChunkSize { get; set; }
        public int ChunkCount { get; set; }
        public string Hash { get; set; } = String.Empty;
    }

    internal sealed class AriesProviderStore
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, AriesProviderMutation> Mutations { get; set; } = new();
        public Dictionary<string, AriesProviderTransfer> Transfers { get; set; } = new();
        public Dictionary<string, bool> MessageReceipts { get; set; } = new();
        public Dictionary<string, AriesProviderRemoteRequest> RemoteRequests { get; set; } = new();
        public Dictionary<string, AriesProviderBulkRequest> BulkRequests { get; set; } = new();
        public Dictionary<string, List<AriesProviderSequenceRange>> BulkSuppressed { get; set; } = new();
        public Dictionary<string, ulong> BulkLateAcknowledgements { get; set; } = new();
    }
    internal sealed class AriesProviderRemoteRequest
    {
        public string TargetDeviceId { get; set; } = String.Empty;
        public string Fingerprint { get; set; } = String.Empty;
        public string Operation { get; set; } = String.Empty;
        public string MessageCursor { get; set; } = "0";
        public Dictionary<long, AriesProviderRemoteEvent> Events { get; set; } = new();
    }
    internal sealed class AriesProviderRemoteEvent
    {
        public string MessageId { get; set; } = String.Empty;
        public string Type { get; set; } = String.Empty;
        public string Payload { get; set; } = String.Empty;
    }
    internal sealed class AriesProviderMutation
    {
        public string Fingerprint { get; set; } = String.Empty;
        public string Key { get; set; } = String.Empty;
        public string Body { get; set; } = String.Empty;
        public string Response { get; set; } = String.Empty;
    }
    internal sealed class AriesProviderTransfer
    {
        public string LocalId { get; set; } = String.Empty;
        public string ServerId { get; set; } = String.Empty;
        public string NodeId { get; set; } = String.Empty;
        public string GroupId { get; set; } = String.Empty;
        public string SenderId { get; set; } = String.Empty;
        public ulong Epoch { get; set; }
        public string Envelope { get; set; } = String.Empty;
        public AriesFileManifest Manifest { get; set; } = new();
        public string[] Targets { get; set; } = Array.Empty<string>();
        public int TtlSeconds { get; set; } = 604800;
        public bool Outgoing { get; set; }
        public bool Completed { get; set; }
        public bool Acknowledged { get; set; }
        public Dictionary<int, string> Chunks { get; set; } = new();
        public HashSet<int> UploadedChunks { get; set; } = new();
    }

    internal static partial class AriesCrypto
    {
        internal static string ContentContext(AriesContentEnvelope e, string type = null, int? index = null)
        {
            if (e.Protocol != "dafeiyu-aries-content" || e.Version != 1 || e.Epoch == 0 ||
                (e.Type != "message" && e.Type != "file"))
                throw new CryptographicException("Unsupported content envelope.");
            foreach (string value in new[] { e.NodeId, e.GroupId, e.SenderId, e.ObjectId })
                if (String.IsNullOrEmpty(value) || value.Length > 128 ||
                    Array.Exists(value.ToCharArray(), Char.IsControl))
                    throw new CryptographicException("Invalid content context.");
            string context = String.Join("\n", e.Protocol, "1", type ?? e.Type, e.NodeId,
                e.GroupId, e.Epoch.ToString(CultureInfo.InvariantCulture), e.SenderId, e.ObjectId);
            return index.HasValue ? context + "\n" + index.Value.ToString(CultureInfo.InvariantCulture) : context;
        }

        private static byte[] ContentCipher(byte[] key, string type)
        {
            if (key.Length != 32) throw new CryptographicException("Invalid content key.");
            return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("dafeiyu-aries-content\n1\n" + type));
        }
        private static string ContentSignature(string context, byte[] nonce, byte[] ciphertext, byte[] tag)
            => String.Join("\n", context, Encode(nonce), Encode(ciphertext), Encode(tag));

        internal static string SealContent(byte[] seed, byte[] key, AriesContentEnvelope e, byte[] plaintext)
        {
            string context = ContentContext(e);
            byte[] nonce = RandomNumberGenerator.GetBytes(12), ciphertext = new byte[plaintext.Length], tag = new byte[16];
            byte[] cipherKey = ContentCipher(key, e.Type);
            try
            {
                using var cipher = new AesGcm(cipherKey, 16);
                cipher.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(context));
                e.Nonce = Encode(nonce); e.Ciphertext = Encode(ciphertext); e.Tag = Encode(tag);
                e.Signature = Sign(seed, ContentSignature(context, nonce, ciphertext, tag));
                return Encode(JsonSerializer.SerializeToUtf8Bytes(e, Json));
            }
            finally { CryptographicOperations.ZeroMemory(cipherKey); }
        }
        internal static AriesContentEnvelope ReadContent(string encoded)
        {
            if (encoded == null || encoded.Length > 1500000) throw new CryptographicException("Content too large.");
            var e = JsonSerializer.Deserialize<AriesContentEnvelope>(Decode(encoded), Json)
                ?? throw new CryptographicException("Missing envelope.");
            ContentContext(e);
            return e;
        }
        internal static byte[] OpenContent(byte[] key, AriesContentEnvelope e, string publicKey)
        {
            string context = ContentContext(e);
            byte[] nonce = Decode(e.Nonce), ciphertext = Decode(e.Ciphertext), tag = Decode(e.Tag);
            if (nonce.Length != 12 || tag.Length != 16) throw new CryptographicException("Invalid GCM parameters.");
            CheckIdentity(e.SenderId, publicKey);
            Verify(publicKey, ContentSignature(context, nonce, ciphertext, tag), e.Signature);
            byte[] cipherKey = ContentCipher(key, e.Type), plaintext = new byte[ciphertext.Length];
            try
            {
                using var cipher = new AesGcm(cipherKey, 16);
                cipher.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(context));
                return plaintext;
            }
            catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
            finally { CryptographicOperations.ZeroMemory(cipherKey); }
        }
        internal static byte[] SealChunk(byte[] seed, byte[] key, AriesContentEnvelope e, int index, byte[] plaintext)
        {
            string context = ContentContext(e, "chunk", index);
            byte[] nonce = RandomNumberGenerator.GetBytes(12), ciphertext = new byte[plaintext.Length], tag = new byte[16];
            byte[] cipherKey = ContentCipher(key, "chunk");
            try
            {
                using var cipher = new AesGcm(cipherKey, 16);
                cipher.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(context));
                byte[] signature = Decode(Sign(seed, ContentSignature(context, nonce, ciphertext, tag)));
                byte[] raw = new byte[plaintext.Length + 92];
                nonce.CopyTo(raw, 0); ciphertext.CopyTo(raw, 12); tag.CopyTo(raw, 12 + ciphertext.Length);
                signature.CopyTo(raw, 28 + ciphertext.Length);
                return raw;
            }
            finally { CryptographicOperations.ZeroMemory(cipherKey); }
        }
        internal static byte[] OpenChunk(byte[] key, AriesContentEnvelope e, string publicKey, int index, byte[] raw)
        {
            if (raw.Length < 92 || raw.Length > 4194304) throw new CryptographicException("Invalid chunk size.");
            string context = ContentContext(e, "chunk", index);
            byte[] nonce = raw.AsSpan(0, 12).ToArray(), ciphertext = raw.AsSpan(12, raw.Length - 92).ToArray();
            byte[] tag = raw.AsSpan(raw.Length - 80, 16).ToArray();
            CheckIdentity(e.SenderId, publicKey);
            Verify(publicKey, ContentSignature(context, nonce, ciphertext, tag), Encode(raw.AsSpan(raw.Length - 64).ToArray()));
            byte[] cipherKey = ContentCipher(key, "chunk"), plaintext = new byte[ciphertext.Length];
            try
            {
                using var cipher = new AesGcm(cipherKey, 16);
                cipher.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(context));
                return plaintext;
            }
            catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
            finally { CryptographicOperations.ZeroMemory(cipherKey); }
        }
    }
}
