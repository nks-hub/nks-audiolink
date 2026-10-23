using NksAudioLink.Cli;

namespace NksAudioLink.Client.Tests;

public sealed class CommandLineOptionsTests
{
    [Theory]
    [InlineData("discover")]
    [InlineData("send")]
    [InlineData("test-tone")]
    public void ExplicitPortOverridesConfigurationInEitherArgumentOrder(string command)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"Server\":\"127.0.0.2\",\"Port\":48000,\"Gain\":0.5}");
            var before = CommandLineOptions.Parse([command, "--port", "49000", "--config", path]);
            var after = CommandLineOptions.Parse([command, "--config", path, "--port", "49000"]);
            Assert.Equal(before, after);
            Assert.Equal(49000, before.Config.Port);
            Assert.Equal("127.0.0.2", before.Config.Server);
            Assert.Equal(0.5, before.Config.Gain);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SendFlagsOverrideConfigurationAndPreserveCaptureOptions()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"Server\":\"127.0.0.2\",\"Gain\":0.5,\"TargetLatencyMs\":100}");
            var options = CommandLineOptions.Parse(["send", "--server", "127.0.0.1", "--gain", "1.5",
                "--latency", "30", "--mode", "capture", "--device", "device-id", "--seconds", "10",
                "--takeover", "true", "--config", path]);
            Assert.Equal("127.0.0.1", options.Config.Server);
            Assert.Equal(1.5, options.Config.Gain);
            Assert.Equal(30, options.Config.TargetLatencyMs);
            Assert.False(options.Loopback);
            Assert.True(options.Takeover);
            Assert.Equal("device-id", options.DeviceId);
            Assert.Equal(10, options.Seconds);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("send", "--mode", "invalid")]
    [InlineData("send", "--seconds", "-1")]
    [InlineData("test-tone", "--seconds", "0")]
    [InlineData("discover", "--server", "127.0.0.1")]
    [InlineData("send", "--unknown", "value")]
    public void InvalidOptionsAreRejected(string command, string option, string value) =>
        Assert.ThrowsAny<ArgumentException>(() => CommandLineOptions.Parse([command, option, value]));

    [Fact]
    public void MissingValueNamesTheOption() =>
        Assert.Contains("--port", Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(["send", "--port"])).Message);
}
