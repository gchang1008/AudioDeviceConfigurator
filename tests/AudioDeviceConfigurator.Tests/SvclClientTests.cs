using AudioDeviceConfigurator.Abstractions;
using AudioDeviceConfigurator.Domain;
using AudioDeviceConfigurator.Svcl;
using AudioDeviceConfigurator.Tests.Fakes;

namespace AudioDeviceConfigurator.Tests;

public sealed class SvclClientTests
{
    private const string SvclPath = @"C:\App\svcl.exe";
    private const string DeviceId = "{endpoint}";

    [Fact]
    public void SaveDeviceFormatParsesExtensibleEffectiveBitsAndMask()
    {
        var fileSystem = InstalledFileSystem();
        var process = new FakeProcessRunner((arguments, fs) =>
        {
            fs.WriteAllBytes(arguments[2], BuildExtensible(6, 48000, 32, 24, 0x3f));
            return new ProcessResult(0, "", "");
        }, fileSystem);
        var client = new SvclClient(process, fileSystem, SvclPath);

        var format = client.SaveDeviceFormat(DeviceId);

        Assert.Equal(6, format.Channels);
        Assert.Equal(48000, format.SampleRate);
        Assert.Equal(32, format.ContainerBits);
        Assert.Equal(24, format.EffectiveBits);
        Assert.Equal(0x3fu, format.ChannelMask);
    }

    [Theory]
    [InlineData(2, "0x3")]
    [InlineData(4, "0x33")]
    [InlineData(6, "0x3f")]
    [InlineData(8, "0x63f")]
    public void SetSpeakersConfigUsesStandardMask(int channels, string mask)
    {
        var fileSystem = InstalledFileSystem();
        var process = new FakeProcessRunner((_, _) => new ProcessResult(0, "", ""), fileSystem);
        var client = new SvclClient(process, fileSystem, SvclPath);

        client.SetSpeakersConfig(DeviceId, channels);

        Assert.Equal(new[] { "/SetSpeakersConfig", DeviceId, mask, mask, mask }, process.Invocations.Single());
    }

    [Fact]
    public void ExitZeroWithNoItemsFoundIsFailure()
    {
        var fileSystem = InstalledFileSystem();
        var process = new FakeProcessRunner(
            (_, _) => new ProcessResult(0, "No items found", ""), fileSystem);
        var client = new SvclClient(process, fileSystem, SvclPath);

        var error = Assert.Throws<SvclException>(() => client.SetDefaultFormat(DeviceId, 24, 48000, 2));

        Assert.Contains("No items found", error.Message);
    }

    [Fact]
    public void UnsupportedSpeakerCountIsRejectedBeforeProcessCall()
    {
        var fileSystem = InstalledFileSystem();
        var process = new FakeProcessRunner((_, _) => new ProcessResult(0, "", ""), fileSystem);
        var client = new SvclClient(process, fileSystem, SvclPath);

        Assert.Throws<ArgumentOutOfRangeException>(() => client.SetSpeakersConfig(DeviceId, 3));
        Assert.Empty(process.Invocations);
    }

    private static FakeFileSystem InstalledFileSystem()
    {
        var result = new FakeFileSystem();
        result.Files[SvclPath] = [];
        result.FileVersions[SvclPath] = "1.28";
        return result;
    }

    internal static byte[] BuildExtensible(
        ushort channels,
        int sampleRate,
        ushort containerBits,
        ushort validBits,
        uint channelMask)
    {
        var data = new byte[40];
        BitConverter.GetBytes((ushort)SavedFormat.WaveFormatExtensible).CopyTo(data, 0);
        BitConverter.GetBytes(channels).CopyTo(data, 2);
        BitConverter.GetBytes((uint)sampleRate).CopyTo(data, 4);
        BitConverter.GetBytes(containerBits).CopyTo(data, 14);
        BitConverter.GetBytes((ushort)22).CopyTo(data, 16);
        BitConverter.GetBytes(validBits).CopyTo(data, 18);
        BitConverter.GetBytes(channelMask).CopyTo(data, 20);
        return data;
    }
}
