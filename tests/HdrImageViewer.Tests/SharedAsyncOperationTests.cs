using HdrImageViewer.Services;
using Xunit;

namespace HdrImageViewer.Tests;

public sealed class SharedAsyncOperationTests
{
    [Fact]
    public async Task WaitAsync_CancelingOneWaiterKeepsSharedWorkAlive()
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var operation = new SharedAsyncOperation<int>(token =>
        {
            started.SetResult(token);
            return result.Task.WaitAsync(token);
        });
        using var firstCancellation = new CancellationTokenSource();

        var firstWaiter = operation.WaitAsync(firstCancellation.Token);
        var secondWaiter = operation.WaitAsync();
        var sharedToken = await started.Task;

        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstWaiter);
        Assert.False(sharedToken.IsCancellationRequested);

        result.SetResult(42);
        Assert.Equal(42, await secondWaiter);
    }

    [Fact]
    public async Task WaitAsync_CancelsSharedWorkAfterLastWaiterLeaves()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharedCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var operation = new SharedAsyncOperation<int>(async token =>
        {
            using var registration = token.Register(() => sharedCancellation.TrySetResult());
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        });
        using var waiterCancellation = new CancellationTokenSource();

        var waiter = operation.WaitAsync(waiterCancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        waiterCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        await sharedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(operation.IsAbandoned);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync());
    }
}
