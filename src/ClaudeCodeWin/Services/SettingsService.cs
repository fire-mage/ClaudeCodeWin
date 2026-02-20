using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ClaudeCodeWin.Models;

namespace ClaudeCodeWin.Services;

public class SettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeCodeWin");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
            return new AppSettings();

        try
        {
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();

            // Auto-migrate plaintext password to DPAPI-protected
            if (!string.IsNullOrEmpty(settings.SshMasterPassword))
            {
                settings.SshMasterPasswordProtected = Protect(settings.SshMasterPassword);
                settings.SshMasterPassword = null;
                Save(settings);
            }

            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDir);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }

    /// <summary>
    /// Encrypt a string using Windows DPAPI (CurrentUser scope). Returns base64.
    /// </summary>
    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        try
        {
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var protectedBytes = DpapiProtect(plainBytes);
            return Convert.ToBase64String(protectedBytes);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Decrypt a DPAPI-protected base64 string. Returns plaintext.
    /// </summary>
    public static string Unprotect(string protected64)
    {
        if (string.IsNullOrEmpty(protected64)) return "";
        try
        {
            var protectedBytes = Convert.FromBase64String(protected64);
            var plainBytes = DpapiUnprotect(protectedBytes);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return "";
        }
    }

    #region DPAPI P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    internal static byte[] DpapiProtect(byte[] data)
    {
        var dataIn = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, dataIn.pbData, data.Length);
        try
        {
            if (!CryptProtectData(ref dataIn, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var dataOut))
                return [];
            try
            {
                var result = new byte[dataOut.cbData];
                Marshal.Copy(dataOut.pbData, result, 0, dataOut.cbData);
                return result;
            }
            finally
            {
                LocalFree(dataOut.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(dataIn.pbData);
        }
    }

    internal static byte[] DpapiUnprotect(byte[] data)
    {
        var dataIn = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, dataIn.pbData, data.Length);
        try
        {
            if (!CryptUnprotectData(ref dataIn, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var dataOut))
                return [];
            try
            {
                var result = new byte[dataOut.cbData];
                Marshal.Copy(dataOut.pbData, result, 0, dataOut.cbData);
                return result;
            }
            finally
            {
                LocalFree(dataOut.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(dataIn.pbData);
        }
    }

    #endregion
}
