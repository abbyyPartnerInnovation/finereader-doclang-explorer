using System.Collections.Concurrent;

namespace Abbyy.DocLang.Demo;

internal sealed class StaWorker : IAsyncDisposable
{
    private readonly BlockingCollection<Action> queue = new();
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public StaWorker()
    {
        var thread = new Thread(() =>
        {
            try { foreach (var action in queue.GetConsumingEnumerable()) action(); }
            finally { stopped.TrySetResult(); }
        }) { IsBackground = true, Name = "FineReader Demo STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
    public Task<T> InvokeAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Add(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        return completion.Task;
    }
    public async ValueTask DisposeAsync()
    {
        queue.CompleteAdding();
        await stopped.Task.ConfigureAwait(false);
        queue.Dispose();
    }
}
