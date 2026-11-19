using Wagterm.Core;

namespace Wagterm.Core.Tests;

public class TerminalConfigTests
{
    [Fact]
    public void Defaults_WhenEmpty()
    {
        var c = TerminalConfig.Parse("");
        Assert.Equal("MesloLGSDZ Nerd Font", c.FontFamily);
        Assert.Equal(16, c.FontSize);
        Assert.Equal(CursorStyle.Bar, c.CursorStyle);
        Assert.True(c.CursorBlink);
        Assert.Equal(530, c.CursorBlinkMs);
        Assert.True(c.PasteProtection);   // WT/Ghostty default: warn on risky pastes
        Assert.True(c.ClipboardWrite);    // OSC 52 writes allowed by default
    }

    [Fact]
    public void ClipboardPolicyKeys_Parse()
    {
        var c = TerminalConfig.Parse("paste-protection = false\nclipboard-write = false\n");
        Assert.False(c.PasteProtection);
        Assert.False(c.ClipboardWrite);
        var on = TerminalConfig.Parse("paste-protection = on\nclipboard-write = yes\n");
        Assert.True(on.PasteProtection);
        Assert.True(on.ClipboardWrite);
    }

    [Fact]
    public void Template_DocumentsClipboardPolicyKeys()
    {
        Assert.Contains("paste-protection", TerminalConfig.DefaultText);
        Assert.Contains("clipboard-write", TerminalConfig.DefaultText);
    }

    [Fact]
    public void NotificationFlash_ParsesValidValuesRejectsUnknown()
    {
        Assert.Equal("none", TerminalConfig.Parse("").NotificationFlash);   // default: off (agterm #215)
        Assert.Equal("once", TerminalConfig.Parse("notification-flash = once").NotificationFlash);
        Assert.Equal("until-focused", TerminalConfig.Parse("notification-flash = until-focused").NotificationFlash);
        Assert.Equal("none", TerminalConfig.Parse("notification-flash = bogus").NotificationFlash);
        Assert.Contains("notification-flash", TerminalConfig.DefaultText);
    }

    [Fact]
    public void NotificationDuration_DefaultsToTenSeconds()
    {
        Assert.Equal(10, TerminalConfig.Parse("").NotificationDuration);
        Assert.Equal(10, TerminalConfig.Parse(TerminalConfig.DefaultText).NotificationDuration);
        Assert.Contains("notification-duration = 10", TerminalConfig.DefaultText);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("30", 30)]
    [InlineData("120", 120)]
    [InlineData("0", 1)]
    [InlineData("-1", 1)]
    [InlineData("121", 120)]
    [InlineData("2147483647", 120)]
    [InlineData("2147483648", 10)]
    [InlineData("invalid", 10)]
    [InlineData("1.5", 10)]
    [InlineData("", 10)]
    public void NotificationDuration_ParsesAndClampsSeconds(string value, int expected)
        => Assert.Equal(expected, TerminalConfig.Parse($"notification-duration = {value}").NotificationDuration);

    [Fact]
    public void NotificationBadgeColors_HaveDefaultsAndParseOverrides()
    {
        var defaults = TerminalConfig.Parse("");
        Assert.Equal("#3DC759", defaults.NotificationColorOk);
        Assert.Equal("#F2B833", defaults.NotificationColorNormal);
        Assert.Equal("#E64D3D", defaults.NotificationColorAttention);

        var custom = TerminalConfig.Parse("notification-color-ok = #112233\nnotification-color-normal = #445566\nnotification-color-attention = #778899");
        Assert.Equal("#112233", custom.NotificationColorOk);
        Assert.Equal("#445566", custom.NotificationColorNormal);
        Assert.Equal("#778899", custom.NotificationColorAttention);
        Assert.Contains("notification-color-ok = #3DC759", TerminalConfig.DefaultText);
        Assert.Contains("notification-color-normal = #F2B833", TerminalConfig.DefaultText);
        Assert.Contains("notification-color-attention = #E64D3D", TerminalConfig.DefaultText);
    }

    [Fact]
    public void ClaudeUpdateCheck_ParsesAndIsDocumented()
    {
        Assert.True(TerminalConfig.Parse("").ClaudeUpdateCheck);   // default: awareness on, update stays manual
        Assert.False(TerminalConfig.Parse("claude-update-check = false").ClaudeUpdateCheck);
        Assert.True(TerminalConfig.Parse("claude-update-check = on").ClaudeUpdateCheck);
        Assert.Contains("claude-update-check", TerminalConfig.DefaultText);
    }

    [Fact]
    public void SessionHost_ParsesValidValuesRejectsUnknown()
    {
        Assert.Equal("in-process", TerminalConfig.Parse("").SessionHost);   // default: today's model
        Assert.Equal("server", TerminalConfig.Parse("session-host = server").SessionHost);
        Assert.Equal("in-process", TerminalConfig.Parse("session-host = bogus").SessionHost);
        Assert.Contains("session-host", TerminalConfig.DefaultText);
    }

    [Fact]
    public void Conpty_DefaultsToInbox_ParsesBundled_RejectsUnknown()
    {
        Assert.Equal("inbox", TerminalConfig.Parse("").Conpty);
        Assert.Equal("bundled", TerminalConfig.Parse("conpty = bundled").Conpty);
        Assert.Equal("inbox", TerminalConfig.Parse("conpty = inbox").Conpty);
        Assert.Equal("inbox", TerminalConfig.Parse("conpty = bogus").Conpty);
        Assert.Equal("inbox", TerminalConfig.Parse(TerminalConfig.DefaultText).Conpty);
    }

    [Fact]
    public void UpdateCheck_ParsesAndIsDocumented()
    {
        Assert.True(TerminalConfig.Parse("").UpdateCheck);         // default: awareness on, applying stays manual
        Assert.False(TerminalConfig.Parse("update-check = false").UpdateCheck);
        Assert.Contains("update-check", TerminalConfig.DefaultText);
        // The two knobs are independent.
        var c = TerminalConfig.Parse("update-check = false\nclaude-update-check = true\n");
        Assert.False(c.UpdateCheck);
        Assert.True(c.ClaudeUpdateCheck);
    }

    [Fact]
    public void ParsesValues_IgnoresCommentsAndUnknown()
    {
        var c = TerminalConfig.Parse(
            "# a comment\nfont-family = Cascadia Mono\nfont-size=18\ncursor-style = block\ncursor-blink = off\nbogus = xyz\n");
        Assert.Equal("Cascadia Mono", c.FontFamily);
        Assert.Equal(18, c.FontSize);
        Assert.Equal(CursorStyle.Block, c.CursorStyle);
        Assert.False(c.CursorBlink);
    }

    [Fact]
    public void CursorStyle_Aliases()
    {
        Assert.Equal(CursorStyle.Bar, TerminalConfig.Parse("cursor-style=beam").CursorStyle);
        Assert.Equal(CursorStyle.Underline, TerminalConfig.Parse("cursor-style=underscore").CursorStyle);
        Assert.Equal(CursorStyle.Block, TerminalConfig.Parse("cursor-style=box").CursorStyle);
    }

    [Fact]
    public void DefaultText_IsParseableToDefaults()
    {
        var c = TerminalConfig.Parse(TerminalConfig.DefaultText);
        Assert.Equal(CursorStyle.Bar, c.CursorStyle);
        Assert.True(c.CursorBlink);
        Assert.Equal(16, c.FontSize);
    }
}
