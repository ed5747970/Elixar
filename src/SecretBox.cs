// Keeping sign-ins and app passwords secret on disk.
// Windows: DPAPI (tied to your Windows account). Linux: AES-256 with an HMAC-SHA256 seal, under a random key kept in
// your desktop keyring (GNOME Keyring / KWallet, through "secret-tool"). A copy of the file alone (a backup, a synced
// home folder, a stolen disk) can't be read. With no keyring, the file stays readable only by your account (0600).

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CDriveCleaner
{
    public static class SecretBox
    {
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("EXV1");
        const string Attr = "application elixar purpose sign-in-key";
        public static Func<byte[]> KeyOverride;   // tests
        static byte[] cached;
        static DateTime failedAt = DateTime.MinValue;   // no keyring: don't ask again for a while

        public static bool IsSealed(byte[] b) { return b != null && b.Length >= Magic.Length && b.Take(Magic.Length).SequenceEqual(Magic); }

        // 64 bytes: 32 to encrypt, 32 to seal. Made once and kept in the keyring.
        static byte[] Key(bool create)
        {
            if (KeyOverride != null) return KeyOverride();
            if (cached != null) return cached;
            if (!Lx.On || Lx.Tool("secret-tool") == null || (DateTime.Now - failedAt).TotalMinutes < 10) return null;
            string o;
            if (Lx.Run("secret-tool", "lookup " + Attr, 60000, out o) == 0)
                try { var k = Convert.FromBase64String(o.Trim()); if (k.Length == 64) return cached = k; } catch { }
            if (!create) return null;
            failedAt = DateTime.Now;   // cleared below when the new key is stored
            var nk = new byte[64];
            using (var r = new RNGCryptoServiceProvider()) r.GetBytes(nk);
            if (Lx.RunInput("secret-tool", "store --label=\"Elixar sign-in key\" " + Attr, Convert.ToBase64String(nk), 60000, out o) != 0) return null;
            // read it back: only a key the keyring really holds is used
            if (Lx.Run("secret-tool", "lookup " + Attr, 60000, out o) != 0 || o.Trim() != Convert.ToBase64String(nk)) return null;
            failedAt = DateTime.MinValue;
            return cached = nk;
        }

        public static bool KeyringReady { get { return Key(false) != null; } }

        public static byte[] Protect(byte[] data, byte[] entropy)
        {
            if (Win.IsWindows) return ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser);
            var key = Key(true);
            if (key == null) return data;                  // no keyring: the 0600 file is the protection
            return Seal(data, entropy, key);
        }

        public static byte[] Unprotect(byte[] data, byte[] entropy)
        {
            if (Win.IsWindows) return ProtectedData.Unprotect(data, entropy, DataProtectionScope.CurrentUser);
            if (!IsSealed(data)) return data;               // saved before 2.2, or without a keyring
            var key = Key(false);
            if (key == null) throw new CryptographicException("The desktop keyring is locked or missing, so the sign-in can't be read.");
            return Open(data, entropy, key);
        }

        // a file saved before 2.2 (or before a keyring existed) is sealed the next time it is read
        public static void Upgrade(string path, byte[] raw, byte[] entropy)
        {
            if (Win.IsWindows || IsSealed(raw)) return;
            if (Key(true) == null) { Lx.Private(path, false); return; }   // no keyring: at least only you can read it
            try { System.IO.File.WriteAllBytes(path, Protect(raw, entropy)); Lx.Private(path, false); } catch { }
        }

        public static byte[] Seal(byte[] data, byte[] entropy, byte[] key)
        {
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256; aes.Key = key.Take(32).ToArray(); aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                aes.GenerateIV();
                byte[] ct;
                using (var enc = aes.CreateEncryptor()) ct = enc.TransformFinalBlock(data, 0, data.Length);
                var head = Magic.Concat(aes.IV).Concat(ct).ToArray();
                return head.Concat(Mac(key, entropy, head)).ToArray();
            }
        }

        public static byte[] Open(byte[] blob, byte[] entropy, byte[] key)
        {
            if (!IsSealed(blob) || blob.Length < Magic.Length + 16 + 16 + 32) throw new CryptographicException("Not an Elixar sealed file.");
            var head = blob.Take(blob.Length - 32).ToArray();
            var mac = blob.Skip(blob.Length - 32).ToArray();
            var want = Mac(key, entropy, head);
            int diff = 0;
            for (int i = 0; i < 32; i++) diff |= mac[i] ^ want[i];   // same time whatever matches
            if (diff != 0) throw new CryptographicException("The file was changed or the key is different.");
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256; aes.Key = key.Take(32).ToArray(); aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                aes.IV = head.Skip(Magic.Length).Take(16).ToArray();
                using (var dec = aes.CreateDecryptor()) return dec.TransformFinalBlock(head, Magic.Length + 16, head.Length - Magic.Length - 16);
            }
        }

        static byte[] Mac(byte[] key, byte[] entropy, byte[] head)
        {
            using (var h = new HMACSHA256(key.Skip(32).Take(32).ToArray()))
            {
                var e = entropy ?? new byte[0];
                var buf = BitConverter.GetBytes(e.Length).Concat(e).Concat(head).ToArray();
                return h.ComputeHash(buf);
            }
        }
    }
}
