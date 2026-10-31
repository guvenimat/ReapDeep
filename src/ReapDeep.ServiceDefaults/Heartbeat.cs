namespace ReapDeep.ServiceDefaults;

public static class Heartbeat
{
    public static readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "reapdeep-heartbeat");
    static long _lastTicks;

    public static void Beat()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastTicks) < 5000) return;
        Interlocked.Exchange(ref _lastTicks, now);
        try
        {
            File.WriteAllText(Path, DateTime.UtcNow.ToString("O"));
        }
        catch (IOException)
        {
        }
    }
}
