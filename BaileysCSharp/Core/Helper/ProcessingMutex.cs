namespace BaileysCSharp.Core.Helper
{
    public class ProcessingMutex
    {
        SemaphoreSlim semaphoreSlim = new SemaphoreSlim(1, 1);
        public ProcessingMutex()
        {

        }

        /// <summary>
        /// Reports exceptions that this mutex would otherwise swallow. The host app points this at
        /// its logger. Previously the catch below was completely empty, so any failure while
        /// processing an inbound message (decrypt, receipt, upsert) vanished without a trace -
        /// the message was simply never delivered and nothing was recorded anywhere.
        /// </summary>
        public static Action<Exception>? OnException { get; set; }

        public async Task Mutex(Action action)
        {
            await semaphoreSlim.WaitAsync();
            try
            {
                action();
            }
            catch (Exception ex)
            {
                try
                {
                    OnException?.Invoke(ex);
                }
                catch
                {
                    // ignored - reporting must never break the caller
                }
            }
            finally
            {
                semaphoreSlim.Release();
            }
        }
    }
}
