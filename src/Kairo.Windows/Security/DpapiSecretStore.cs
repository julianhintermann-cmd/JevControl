using System.Security.Cryptography;
using System.Text;
using Kairo.Core.Abstractions;
using Kairo.Core.History;
using Kairo.Core.Telemetry;

namespace Kairo.Windows.Security;

/// <summary>
/// Stores secrets (the OpenRouter API key) encrypted with Windows DPAPI, bound to the current Windows user
/// (DataProtectionScope.CurrentUser) plus per-installation entropy. Plain text never touches the disk;
/// on deletion the blob is overwritten with random bytes before it is removed.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore, IDataProtector
{
    private static readonly byte[] AppEntropy = "Kairo/v1/{8C1D6A0F-4F7B-4E1B-9E7C-5A3F0D2B7C41}"u8.ToArray();
    private readonly string _directory;
    private readonly KairoLogger _log;
    private readonly object _lock = new();
    private byte[]? _entropy;

    public DpapiSecretStore(string directory, KairoLogger log)
    {
        _directory = directory;
        _log = log;
    }

    private string PathFor(string name)
    {
        var safe = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (safe.Length == 0) { throw new ArgumentException("Ungültiger Name.", nameof(name)); }
        return Path.Combine(_directory, safe + ".dpapi");
    }

    /// <summary>Per-installation entropy (random, created on first use). It is not a secret by itself.</summary>
    private byte[] Entropy()
    {
        lock (_lock)
        {
            if (_entropy is not null) { return _entropy; }
            Directory.CreateDirectory(_directory);
            var file = Path.Combine(_directory, "entropy.bin");
            byte[] salt;
            if (File.Exists(file) && new FileInfo(file).Length == 32)
            {
                salt = File.ReadAllBytes(file);
            }
            else
            {
                salt = RandomNumberGenerator.GetBytes(32);
                File.WriteAllBytes(file, salt);
                TryHide(file);
            }
            _entropy = AppEntropy.Concat(salt).ToArray();
            return _entropy;
        }
    }

    public bool HasSecret(string name) => File.Exists(PathFor(name));

    public string? GetSecret(string name)
    {
        var file = PathFor(name);
        if (!File.Exists(file)) { return null; }
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy(), DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (CryptographicException)
        {
            // Blob of another user/machine or corrupted – treat as missing (never log contents).
            _log.Warn("secrets", $"secret '{name}' could not be decrypted");
            return null;
        }
    }

    public void SetSecret(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Directory.CreateDirectory(_directory);
        var plain = Encoding.UTF8.GetBytes(value.Trim());
        try
        {
            var blob = ProtectedData.Protect(plain, Entropy(), DataProtectionScope.CurrentUser);
            var file = PathFor(name);
            var temp = file + ".tmp";
            File.WriteAllBytes(temp, blob);
            if (File.Exists(file)) { TaskHistoryStore.SecureDelete(file); }
            File.Move(temp, file, overwrite: true);
            TryHide(file);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
        _log.Info("secrets", $"secret '{name}' stored (DPAPI CurrentUser)");
    }

    public void DeleteSecret(string name)
    {
        TaskHistoryStore.SecureDelete(PathFor(name));
        _log.Info("secrets", $"secret '{name}' removed");
    }

    /// <summary>Removes all secrets and the entropy (used by "remove all data" and uninstall).</summary>
    public void DeleteAll()
    {
        if (!Directory.Exists(_directory)) { return; }
        foreach (var file in Directory.GetFiles(_directory)) { TaskHistoryStore.SecureDelete(file); }
        lock (_lock) { _entropy = null; }
    }

    // IDataProtector for the encrypted task history.
    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy(), DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy(), DataProtectionScope.CurrentUser);

    private static void TryHide(string file)
    {
        try { File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.Hidden); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
