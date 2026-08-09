using System;

namespace JetDatabaseReader.Tests
{
    /// <summary>
    /// <see cref="IProgress{T}"/> that invokes the callback on the reporting thread.
    ///
    /// <see cref="Progress{T}"/> posts to the thread pool, so a test that asserts on what was
    /// reported races with callbacks still in flight, and a test that wants to act during a read —
    /// cancelling it, for instance — cannot rely on the callback arriving in time.
    /// </summary>
    internal sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _action;

        public SyncProgress(Action<T> action) => _action = action;

        public void Report(T value) => _action(value);
    }
}
