namespace Bubo.Schematic.Tests;
public static class Check
{
    public static int Passed { get; private set; }
    public static int Failed { get; private set; }
    public static void Case(string name, Action action)
    {
        try { action(); Passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { Failed++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
    }
    public static void True(bool value, string message = "Expected true")
    { if (!value) throw new InvalidOperationException(message); }
    public static T Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
