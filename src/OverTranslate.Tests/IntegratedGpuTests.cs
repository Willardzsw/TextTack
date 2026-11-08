using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

public class IntegratedGpuTests
{
    private static DirectMlDevice.Adapter Gpu(ulong dedicatedMb) =>
        new(0, "GPU", 0x10DE, 1, false, dedicatedMb << 20);

    [Fact]
    public void AnIntelIrisXeWithItsFewMegabytesIsIntegrated()
    {
        // An Iris Xe reports 128 MB of its own; the rest it borrows from the system.
        Assert.True(DirectMlDevice.IsIntegrated(Gpu(128)));
        Assert.True(DirectMlDevice.IsIntegrated(Gpu(512)));
    }

    [Fact]
    public void ACardWithItsOwnMemoryIsNot()
    {
        Assert.False(DirectMlDevice.IsIntegrated(Gpu(1024)));
        Assert.False(DirectMlDevice.IsIntegrated(Gpu(8188)));   // RTX 4060 Laptop
    }
}
