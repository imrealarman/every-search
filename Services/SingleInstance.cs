namespace EverySearch;

/// <summary>Keeps one copy running; launching a second copy just asks the first to show its palette.</summary>
public static class SingleInstance
{
    private const string Name = @"Local\EverySearch.7C2E41A9";
    private static Mutex? mutex;
    private static EventWaitHandle? signal;

    /// <summary>Returns false (after nudging the running copy) if another instance already owns the app.</summary>
    public static bool Acquire()
    {
        mutex = new Mutex(true, Name + ".lock", out bool owned);
        if (!owned)
        {
            try { EventWaitHandle.OpenExisting(Name + ".show").Set(); } catch { /* the other copy is still starting */ }
            return false;
        }
        signal = new EventWaitHandle(false, EventResetMode.AutoReset, Name + ".show");
        return true;
    }

    /// <summary>Calls <paramref name="onShow"/> (on a background thread) whenever a second launch asks for the palette.</summary>
    public static void Listen(Action onShow)
    {
        var wait = signal;
        if (wait == null) return;
        new Thread(() =>
        {
            while (wait.WaitOne()) onShow();
        })
        { IsBackground = true, Name = "single-instance" }.Start();
    }
}
