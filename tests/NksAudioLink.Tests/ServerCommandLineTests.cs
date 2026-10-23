using NksAudioLink.Server;

namespace NksAudioLink.Tests;

public class ServerCommandLineTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitOptionsOverrideFileRegardlessOfOrder(bool fileFirst)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"port":7355,"httpPort":0,"sink":"alsa:default"}""");
            string[] file = ["--config", path];
            string[] options = ["--port", "17355", "--http-port", "18080", "--http-bind", "127.0.0.2", "--sink", "null"];
            string[] args = fileFirst ? ["run", .. file, .. options] : ["run", .. options, .. file];
            var config = ServerCommandLine.Parse(args);
            Assert.Equal(17355, config.Port);
            Assert.Equal(18080, config.HttpPort);
            Assert.Equal("127.0.0.2", config.HttpBindAddress);
            Assert.Equal("null", config.Sink);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("--http-port", "65536", typeof(InvalidDataException))]
    [InlineData("--http-bind", "*", typeof(InvalidDataException))]
    [InlineData("--unknown", "1", typeof(ArgumentException))]
    public void InvalidOptionsAreRejected(string option, string value, Type exceptionType) =>
        Assert.Throws(exceptionType, () => ServerCommandLine.Parse(["run", option, value]));

    [Fact]
    public void MissingValueIsActionable() =>
        Assert.Contains("needs a value", Assert.Throws<ArgumentException>(() => ServerCommandLine.Parse(["run", "--http-port"])).Message);
}
