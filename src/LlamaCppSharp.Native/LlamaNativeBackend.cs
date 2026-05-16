namespace Bubo.LlamaCppSharp.Native;

public sealed class LlamaNativeBackend : IDisposable
{
    private bool _disposed;

    private LlamaNativeBackend()
    {
    }

    public static LlamaNativeBackend Initialize()
    {
        LlamaNativeMethods.BackendInit();
        return new LlamaNativeBackend();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        LlamaNativeMethods.BackendFree();
        _disposed = true;
    }
}
