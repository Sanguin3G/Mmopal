using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Mmopal.Native;

/// <summary>
/// Encrypts and persists local secrets (such as the GW2 API key) using Windows DPAPI (CurrentUser scope).
/// Plaintext is never written to disk.
/// </summary>
public sealed class WindowsSecretStore
{
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly byte[] DefaultEntropy = Encoding.UTF8.GetBytes("Mmopal.Gw2.SecretStore.v1");

    public static byte[] Protect(byte[] plaintext, byte[]? entropy = null)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (plaintext.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var optionalEntropy = entropy ?? DefaultEntropy;
        var inputBlob = default(DataBlob);
        var entropyBlob = default(DataBlob);
        var outputBlob = default(DataBlob);

        try
        {
            inputBlob = CreateBlob(plaintext);
            entropyBlob = CreateBlob(optionalEntropy);
            var ok = CryptProtectData(
                ref inputBlob,
                "Mmopal Secret",
                ref entropyBlob,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptProtectUiForbidden,
                out outputBlob);
            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows DPAPI could not protect the secret.");
            }

            return CopyBlob(outputBlob);
        }
        finally
        {
            FreeInputBlob(ref inputBlob, zeroMemory: true);
            FreeInputBlob(ref entropyBlob, zeroMemory: false);
            FreeOutputBlob(ref outputBlob, zeroMemory: false);
        }
    }

    public static byte[] Unprotect(byte[] ciphertext, byte[]? entropy = null)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        if (ciphertext.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var optionalEntropy = entropy ?? DefaultEntropy;
        var inputBlob = default(DataBlob);
        var entropyBlob = default(DataBlob);
        var outputBlob = default(DataBlob);

        try
        {
            inputBlob = CreateBlob(ciphertext);
            entropyBlob = CreateBlob(optionalEntropy);
            var ok = CryptUnprotectData(
                ref inputBlob,
                IntPtr.Zero,
                ref entropyBlob,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptProtectUiForbidden,
                out outputBlob);
            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows DPAPI could not unprotect the secret.");
            }

            return CopyBlob(outputBlob);
        }
        finally
        {
            FreeInputBlob(ref inputBlob, zeroMemory: false);
            FreeInputBlob(ref entropyBlob, zeroMemory: false);
            FreeOutputBlob(ref outputBlob, zeroMemory: true);
        }
    }

    public static void SaveText(string filePath, string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            Delete(fullPath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var bytes = Encoding.UTF8.GetBytes(plaintext.Trim());
        try
        {
            var protectedBytes = Protect(bytes);
            File.WriteAllBytes(fullPath, protectedBytes);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    public static string? LoadText(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        try
        {
            var ciphertext = File.ReadAllBytes(fullPath);
            if (ciphertext.Length == 0)
            {
                return null;
            }

            var plaintext = Unprotect(ciphertext);
            try
            {
                var value = Encoding.UTF8.GetString(plaintext).Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            finally
            {
                Array.Clear(plaintext);
            }
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static bool HasSecret(string filePath) =>
        !string.IsNullOrWhiteSpace(filePath) && File.Exists(Path.GetFullPath(filePath));

    public static void Delete(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        var fullPath = Path.GetFullPath(filePath);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
    }

    private static DataBlob CreateBlob(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob { Count = bytes.Length, Data = pointer };
    }

    private static byte[] CopyBlob(DataBlob blob)
    {
        var result = new byte[blob.Count];
        Marshal.Copy(blob.Data, result, 0, result.Length);
        return result;
    }

    /// <summary>Frees a blob this class allocated with <see cref="CreateBlob"/>.</summary>
    private static void FreeInputBlob(ref DataBlob blob, bool zeroMemory) =>
        FreeBlob(ref blob, zeroMemory, Marshal.FreeHGlobal);

    /// <summary>Frees a blob DPAPI allocated (LocalAlloc).</summary>
    private static void FreeOutputBlob(ref DataBlob blob, bool zeroMemory) =>
        FreeBlob(ref blob, zeroMemory, pointer => _ = LocalFree(pointer));

    private static void FreeBlob(ref DataBlob blob, bool zeroMemory, Action<IntPtr> free)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }

        if (zeroMemory && blob.Count > 0)
        {
            ZeroUnmanaged(blob.Data, blob.Count);
        }

        free(blob.Data);
        blob = default;
    }

    private static void ZeroUnmanaged(IntPtr pointer, int count)
    {
        for (var i = 0; i < count; i++)
        {
            Marshal.WriteByte(pointer, i, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Count;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? dataDescr,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr dataDescr,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
