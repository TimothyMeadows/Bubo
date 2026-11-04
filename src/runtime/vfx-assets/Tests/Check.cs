namespace Bubo.Vfx.Tests;

internal static class Check
{
    internal static int Passed { get; private set; }
    internal static List<string> Limits { get; } = [];

    internal static void That(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        Passed++;
        Console.WriteLine("PASS: " + name);
    }

    internal static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { That(true, name); return; }
        throw new InvalidOperationException("FAIL: expected " + typeof(T).Name + ": " + name);
    }
}
