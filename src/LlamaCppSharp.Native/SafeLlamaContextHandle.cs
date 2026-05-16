using System.Runtime.InteropServices;

namespace Bubo.LlamaCppSharp.Native;

public sealed class SafeLlamaContextHandle : SafeHandle
{
    public SafeLlamaContextHandle()
        : base(nint.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == nint.Zero;

    protected override bool ReleaseHandle()
    {
        if (!IsInvalid)
        {
            LlamaNativeMethods.ContextFree(handle);
        }

        return true;
    }
}
