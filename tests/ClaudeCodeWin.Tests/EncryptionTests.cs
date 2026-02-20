using ClaudeCodeWin.Services;

namespace ClaudeCodeWin.Tests;

public class EncryptionTests
{
    [Fact]
    public void DpapiRoundTrip_ReturnsOriginal()
    {
        var original = "SuperSecretPassword123!@#";
        var encrypted = SettingsService.Protect(original);
        var decrypted = SettingsService.Unprotect(encrypted);

        Assert.NotEqual(original, encrypted); // Should be different (base64 blob)
        Assert.Equal(original, decrypted);
    }

    [Fact]
    public void EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", SettingsService.Protect(""));
        Assert.Equal("", SettingsService.Unprotect(""));
    }

    [Fact]
    public void CorruptData_ReturnsEmpty()
    {
        // Invalid base64 or corrupt DPAPI blob should not throw
        Assert.Equal("", SettingsService.Unprotect("not-valid-base64!!!"));
        Assert.Equal("", SettingsService.Unprotect(Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5 })));
    }

    [Fact]
    public void DpapiRoundTrip_Unicode()
    {
        var original = "Пароль с юникодом 🔑";
        var encrypted = SettingsService.Protect(original);
        var decrypted = SettingsService.Unprotect(encrypted);

        Assert.Equal(original, decrypted);
    }

    [Fact]
    public void DpapiRawBytes_RoundTrip()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("test data for raw DPAPI");
        var encrypted = SettingsService.DpapiProtect(data);
        var decrypted = SettingsService.DpapiUnprotect(encrypted);

        Assert.Equal(data, decrypted);
        Assert.NotEqual(data, encrypted); // Encrypted should differ
    }
}
