using Forms = System.Windows.Forms;

namespace Clicky.Windows.Native;

public static class DictationInserter
{
    public static Task InsertAsync(string text, nint expectedWindow, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Task.CompletedTask;
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Forms.IDataObject? previous = null;
            uint ours = 0;
            var clipboardSet = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                NativeMethods.RequireForeground(expectedWindow);
                // Materialize clipboard formats while their owner is available. Failure aborts insertion.
                var original = ClipboardRetry(Forms.Clipboard.GetDataObject, cancellationToken);
                if (original != null)
                {
                    var snapshot = new Forms.DataObject();
                    foreach (var format in original.GetFormats(false))
                    {
                        var value = original.GetData(format, false);
                        if (value is not null)
                            snapshot.SetData(format, false, value);
                    }
                    previous = snapshot;
                }
                ClipboardRetry(() => Forms.Clipboard.SetText(text, Forms.TextDataFormat.UnicodeText), cancellationToken);
                ours = NativeMethods.GetClipboardSequenceNumber();
                clipboardSet = true;
                cancellationToken.ThrowIfCancellationRequested();
                NativeMethods.RequireForeground(expectedWindow);
                // Wait for held global-shortcut modifiers to be released, without changing their state.
                var until = Environment.TickCount64 + 1800;
                while (new[] { 0x11, 0x12, 0x10, 0x5b, 0x5c }.Any(k => (NativeMethods.GetAsyncKeyState(k) & 0x8000) != 0))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Environment.TickCount64 > until)
                        throw new InvalidOperationException("Release shortcut modifier keys before inserting dictation.");
                    Thread.Sleep(20);
                }
                NativeMethods.RequireForeground(expectedWindow);
                if (NativeMethods.GetClipboardSequenceNumber() != ours)
                    throw new InvalidOperationException("Clipboard changed while dictating. Transcript is preserved in history; insertion was cancelled.");
                cancellationToken.ThrowIfCancellationRequested();
                NativeMethods.Send(NativeMethods.Key(0x11), NativeMethods.Key(0x56), NativeMethods.Key(0x56, true), NativeMethods.Key(0x11, true));
                // Most desktop applications read synchronously, but allow deferred clipboard reads.
                Thread.Sleep(350);
                result.TrySetResult();
            }
            catch (OperationCanceledException) { result.TrySetCanceled(cancellationToken); }
            catch (Exception exception) { result.TrySetException(exception); }
            finally
            {
                if (clipboardSet && NativeMethods.GetClipboardSequenceNumber() == ours)
                {
                    try
                    {
                        if (previous is null)
                            Forms.Clipboard.Clear();
                        else
                            Forms.Clipboard.SetDataObject(previous, true, 3, 60);
                    }
                    catch (System.Runtime.InteropServices.ExternalException) { /* Another clipboard owner won the race; do not overwrite it. */ }
                }
            }
        })
        {
            IsBackground = true,
            Name = "Clicky clipboard insertion"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    private static T ClipboardRetry<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return operation();
            }
            catch (System.Runtime.InteropServices.ExternalException) when (attempt < 7) { Thread.Sleep(40); }
        }
    }

    private static void ClipboardRetry(Action operation, CancellationToken cancellationToken)
        => ClipboardRetry(() => { operation(); return true; }, cancellationToken);
}
