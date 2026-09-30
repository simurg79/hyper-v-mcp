using System.Security.Cryptography;
using System.Text;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// In-process implementation of the glibc <c>crypt(3)</c> SHA-512 (<c>$6$</c>) password
/// hashing scheme, per Ulrich Drepper's SHA-crypt specification
/// (https://www.akkadia.org/drepper/SHA-crypt.txt).
///
/// WHY managed/in-process: the seed must carry only the hash, never the plaintext. Shelling out
/// (e.g. <c>openssl passwd</c>) would put the plaintext in a script body that
/// <see cref="PowerShellExecutor"/> writes to a temp <c>.ps1</c> on disk. Hashing here keeps the
/// plaintext in process memory only, and drops the OpenSSL host prerequisite (no external binary,
/// no LINUX_PRECONDITION_UNMET runtime surprise).
/// See internal documentation — ISO-D29.
/// </summary>
internal static class Sha512Crypt
{
    /// <summary>The <c>$6$</c> SHA-512 crypt identifier prefix.</summary>
    private const string Prefix = "$6$";

    /// <summary>Max salt length the scheme honors (glibc caps at 16 characters).</summary>
    private const int MaxSaltLength = 16;

    /// <summary>Default (and only) round count used here; matches the scheme's default.</summary>
    private const int DefaultRounds = 5000;

    // crypt(3)'s bespoke base64 alphabet — NOT standard base64. Order is significant.
    private const string CryptBase64Alphabet =
        "./0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    // Salt characters drawn from the same alphabet so the produced salt is round-trippable.
    private static readonly char[] SaltAlphabet = CryptBase64Alphabet.ToCharArray();

    /// <summary>
    /// Computes the <c>$6$&lt;salt&gt;$&lt;hash&gt;</c> crypt string for
    /// <paramref name="password"/> using a fresh cryptographically-random 16-char salt.
    /// The plaintext bytes are zeroed before returning.
    /// </summary>
    internal static string Hash(string password)
    {
        return Hash(password, GenerateSalt());
    }

    /// <summary>
    /// Computes the <c>$6$&lt;salt&gt;$&lt;hash&gt;</c> crypt string for
    /// <paramref name="password"/> with the supplied <paramref name="salt"/> (truncated to
    /// 16 chars). Exposed for deterministic unit testing against known crypt(3) vectors.
    /// </summary>
    internal static string Hash(string password, string salt)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(salt);

        if (salt.Length > MaxSaltLength)
        {
            salt = salt.Substring(0, MaxSaltLength);
        }

        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var saltBytes = Encoding.UTF8.GetBytes(salt);
        try
        {
            var raw = ComputeDigest(passwordBytes, saltBytes);
            var encoded = EncodeDigest(raw);
            return $"{Prefix}{salt}${encoded}";
        }
        finally
        {
            // Do not leave plaintext-derived bytes on the managed heap.
            Array.Clear(passwordBytes, 0, passwordBytes.Length);
        }
    }

    private static string GenerateSalt()
    {
        var chars = new char[MaxSaltLength];
        for (var iter = 0; iter < chars.Length; iter++)
        {
            chars[iter] = SaltAlphabet[RandomNumberGenerator.GetInt32(SaltAlphabet.Length)];
        }
        return new string(chars);
    }

    /// <summary>
    /// The SHA-crypt digest kernel (spec steps 1–21 + the 1000-round strengthening loop,
    /// step 21). Returns the 64 raw digest bytes prior to the custom base64 encoding.
    /// </summary>
    private static byte[] ComputeDigest(byte[] password, byte[] salt)
    {
        using var sha = SHA512.Create();

        // Digest B (steps 4–8): SHA512(password + salt + password).
        var digestB = Sha(sha, password, salt, password);

        // Digest A (steps 1–3, 9–12): password + salt, then for each block of the
        // password length either the full B digest or its remaining bytes.
        var digestAInput = new List<byte>();
        digestAInput.AddRange(password);
        digestAInput.AddRange(salt);
        AppendRepeated(digestAInput, digestB, password.Length);

        // Steps 13–15: fold the password length in, bit by bit — B on a 1 bit, password on 0.
        for (var length = password.Length; length > 0; length >>= 1)
        {
            if ((length & 1) != 0)
                digestAInput.AddRange(digestB);
            else
                digestAInput.AddRange(password);
        }
        var digestA = Sha(sha, digestAInput.ToArray());

        // Digest DP (steps 16–18): SHA512(password repeated |password| times), giving the
        // per-password sequence P.
        var dpInput = new List<byte>();
        for (var iter = 0; iter < password.Length; iter++)
            dpInput.AddRange(password);
        var digestDP = Sha(sha, dpInput.ToArray());
        var sequenceP = ProduceSequence(digestDP, password.Length);

        // Digest DS (steps 19–21): SHA512(salt repeated 16 + A[0] times), giving sequence S.
        var dsInput = new List<byte>();
        var saltRepeat = 16 + (digestA[0] & 0xff);
        for (var iter = 0; iter < saltRepeat; iter++)
            dsInput.AddRange(salt);
        var digestDS = Sha(sha, dsInput.ToArray());
        var sequenceS = ProduceSequence(digestDS, salt.Length);

        // Step 21: 5000 rounds of re-hashing, alternating P/A and S per the odd/even rules.
        var current = digestA;
        for (var round = 0; round < DefaultRounds; round++)
        {
            var roundInput = new List<byte>();
            if ((round & 1) != 0)
                roundInput.AddRange(sequenceP);
            else
                roundInput.AddRange(current);

            if (round % 3 != 0)
                roundInput.AddRange(sequenceS);

            if (round % 7 != 0)
                roundInput.AddRange(sequenceP);

            if ((round & 1) != 0)
                roundInput.AddRange(current);
            else
                roundInput.AddRange(sequenceP);

            current = Sha(sha, roundInput.ToArray());
        }
        return current;
    }

    /// <summary>Appends <paramref name="count"/> bytes of <paramref name="block"/> (repeating).</summary>
    private static void AppendRepeated(List<byte> target, byte[] block, int count)
    {
        var remaining = count;
        while (remaining > 0)
        {
            var take = Math.Min(remaining, block.Length);
            target.AddRange(block.AsSpan(0, take).ToArray());
            remaining -= take;
        }
    }

    /// <summary>Builds a per-byte sequence of <paramref name="length"/> bytes from a 64-byte digest.</summary>
    private static byte[] ProduceSequence(byte[] digest, int length)
    {
        var sequence = new byte[length];
        for (var iter = 0; iter < length; iter++)
        {
            sequence[iter] = digest[iter % digest.Length];
        }
        return sequence;
    }

    private static byte[] Sha(SHA512 sha, params byte[][] parts)
    {
        var total = 0;
        foreach (var part in parts)
            total += part.Length;
        var buffer = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, buffer, offset, part.Length);
            offset += part.Length;
        }
        return sha.ComputeHash(buffer);
    }

    /// <summary>
    /// Encodes the 64 raw digest bytes using crypt(3)'s permuted 3-bytes→4-chars base64 with
    /// the scheme's fixed byte-triplet ordering for SHA-512 (spec's final table).
    /// </summary>
    private static string EncodeDigest(byte[] digest)
    {
        var builder = new StringBuilder();
        // The SHA-512 permutation table (spec step 22). Each triplet maps three digest
        // indices (b2, b1, b0) into four output characters; the final group carries one byte.
        int[][] groups =
        {
            new[] { 0, 21, 42 }, new[] { 22, 43, 1 }, new[] { 44, 2, 23 }, new[] { 3, 24, 45 },
            new[] { 25, 46, 4 }, new[] { 47, 5, 26 }, new[] { 6, 27, 48 }, new[] { 28, 49, 7 },
            new[] { 50, 8, 29 }, new[] { 9, 30, 51 }, new[] { 31, 52, 10 }, new[] { 53, 11, 32 },
            new[] { 12, 33, 54 }, new[] { 34, 55, 13 }, new[] { 56, 14, 35 }, new[] { 15, 36, 57 },
            new[] { 37, 58, 16 }, new[] { 59, 17, 38 }, new[] { 18, 39, 60 }, new[] { 40, 61, 19 },
            new[] { 62, 20, 41 },
        };
        foreach (var group in groups)
        {
            EncodeTriplet(builder, digest[group[0]], digest[group[1]], digest[group[2]], 4);
        }
        // Final leftover byte (index 63) is emitted as two characters.
        EncodeTriplet(builder, 0, 0, digest[63], 2);
        return builder.ToString();
    }

    private static void EncodeTriplet(StringBuilder builder, int b2, int b1, int b0, int count)
    {
        var value = (b2 << 16) | (b1 << 8) | b0;
        for (var iter = 0; iter < count; iter++)
        {
            builder.Append(CryptBase64Alphabet[value & 0x3f]);
            value >>= 6;
        }
    }
}
