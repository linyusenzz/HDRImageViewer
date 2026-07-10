using HdrImageViewer.Presentation;
using Xunit;

namespace HdrImageViewer.Tests;

public sealed class ImageLoadControllerTests
{
    [Fact]
    public void Begin_CancelsPreviousOperationAndKeepsNewestCurrent()
    {
        using var lifetime = new CancellationTokenSource();
        using var controller = new ImageLoadController(lifetime.Token);
        var first = controller.Begin();
        var second = controller.Begin();

        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(controller.IsCurrent(first));
        Assert.True(controller.IsCurrent(second));

        controller.Complete(first);
        Assert.True(controller.IsCurrent(second));
        controller.Complete(second);
    }

    [Fact]
    public void Dispose_CancelsCurrentOperation()
    {
        using var controller = new ImageLoadController(CancellationToken.None);
        var operation = controller.Begin();
        var token = operation.Token;

        controller.Dispose();

        Assert.True(token.IsCancellationRequested);
        operation.Dispose();
    }
}
