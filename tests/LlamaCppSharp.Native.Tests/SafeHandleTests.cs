using Bubo.LlamaCppSharp.Native;

namespace Bubo.LlamaCppSharp.Native.Tests;

public sealed class SafeHandleTests
{
    [Fact]
    public void SafeHandlesStartInvalid()
    {
        using var model = new SafeLlamaModelHandle();
        using var context = new SafeLlamaContextHandle();
        using var sampler = new SafeLlamaSamplerHandle();

        Assert.True(model.IsInvalid);
        Assert.True(context.IsInvalid);
        Assert.True(sampler.IsInvalid);
    }
}
