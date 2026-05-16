using System.Runtime.InteropServices;

namespace Bubo.LlamaCppSharp.Native;

public sealed class SafeLlamaSamplerHandle : SafeHandle
{
    public SafeLlamaSamplerHandle()
        : base(nint.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == nint.Zero;

    protected override bool ReleaseHandle()
    {
        if (!IsInvalid)
        {
            LlamaNativeMethods.SamplerFree(handle);
        }

        return true;
    }
}
