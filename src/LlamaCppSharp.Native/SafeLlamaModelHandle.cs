using System.Runtime.InteropServices;

namespace Bubo.LlamaCppSharp.Native;

public sealed class SafeLlamaModelHandle : SafeHandle
{
    public SafeLlamaModelHandle()
        : base(nint.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == nint.Zero;

    protected override bool ReleaseHandle()
    {
        if (!IsInvalid)
        {
            LlamaNativeMethods.ModelFree(handle);
        }

        return true;
    }
}
