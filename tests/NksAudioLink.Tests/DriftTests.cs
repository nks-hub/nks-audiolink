using NksAudioLink.Core;

namespace NksAudioLink.Tests;

public class DriftTests
{
    [Theory]
    [InlineData(200)]
    [InlineData(-200)]
    public void ControllerBoundsDriftForTenSimulatedMinutes(int sourcePpm)
    {
        var controller = new DriftController(30);
        double fillMs = 30;
        for (int i = 0; i < 120_000; i++)
        {
            double ratio = controller.Update(fillMs);
            fillMs += 5.0 * (sourcePpm - controller.RatioPpm) / 1_000_000;
            Assert.InRange(ratio, 0.995, 1.005);
        }
        Assert.InRange(fillMs, 25, 35);
        Assert.InRange(controller.RatioPpm, sourcePpm - 50, sourcePpm + 50);
    }

    [Fact]
    public void ResamplerInterpolatesContinuousSine()
    {
        var resampler = new DriftResampler();
        short[] input = new short[4000 * 2];
        for (int i = 0; i < 4000; i++)
        {
            short sample = (short)Math.Round(12000 * Math.Sin(2 * Math.PI * 1000 * i / 48000));
            input[2 * i] = sample;
            input[2 * i + 1] = sample;
        }
        resampler.Push(input);
        short[] output = new short[3990 * 2];
        int written = resampler.Read(output, 1.002);
        Assert.InRange(written, 3900, 3990);
        for (int i = 1; i < written; i++)
            Assert.InRange(Math.Abs(output[2 * i] - output[2 * (i - 1)]), 0, 1600);
    }
}
