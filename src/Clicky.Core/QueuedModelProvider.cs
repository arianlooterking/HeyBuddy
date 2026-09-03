namespace Clicky.Core;

public sealed class QueuedModelProvider(IModelProvider inner, SemaphoreSlim gate) : IModelProvider
{
    public string Name => inner.Name;
    public bool IsCloud => inner.IsCloud;
    public async Task<ModelReply> CompleteAsync(ModelRequest request, Action<string>? onText, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await inner.CompleteAsync(request, onText, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
