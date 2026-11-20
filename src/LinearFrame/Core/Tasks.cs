namespace LinearFrame.Core;

internal static class TaskExtensions
{
    /// <summary>
    /// For work nobody waits for - the error output of a helper process read on the side, a reading that was cancelled
    /// half way. If it fails (the process was killed, the pipe closed under it), that is nobody's news; without this the
    /// failure turned up later as an "Unobserved task" error in the log.
    /// </summary>
    public static void Forget(this Task task) =>
        task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
