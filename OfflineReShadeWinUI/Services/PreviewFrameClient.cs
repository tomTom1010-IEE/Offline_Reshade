using System.IO.Pipes;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;

namespace OfflineReShade.WinUI.Services;

public sealed class PreviewFrameClient : IDisposable
{
    private const uint Magic = 0x4650524f;
    private readonly DispatcherQueue _dispatcher;
    private readonly object _frameLock = new();
    private CancellationTokenSource? _cancellation;
    private Task? _readTask;
    private byte[]? _pendingPixels;
    private int _pendingWidth;
    private int _pendingHeight;
    private bool _applyQueued;
    private WriteableBitmap? _bitmap;

    public PreviewFrameClient()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    public event Action<WriteableBitmap>? FrameReceived;
    public event Action<string>? ErrorReceived;

    public void Start(string pipeName)
    {
        Stop();
        _cancellation = new CancellationTokenSource();
        _readTask = Task.Run(() => ReadLoopAsync(pipeName, _cancellation.Token));
    }

    public void Stop()
    {
        var cancellation = _cancellation;
        _cancellation = null;
        if (cancellation == null)
            return;

        try
        {
            cancellation.Cancel();
        }
        finally
        {
            cancellation.Dispose();
        }

        lock (_frameLock)
        {
            _pendingPixels = null;
            _pendingWidth = 0;
            _pendingHeight = 0;
            _applyQueued = false;
        }
        _bitmap = null;
    }

    private async Task ReadLoopAsync(string pipeName, CancellationToken cancellationToken)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(30000, cancellationToken);

            var header = new byte[16];
            while (!cancellationToken.IsCancellationRequested)
            {
                await ReadExactAsync(pipe, header, cancellationToken);
                var magic = BitConverter.ToUInt32(header, 0);
                var width = BitConverter.ToInt32(header, 4);
                var height = BitConverter.ToInt32(header, 8);
                var byteCount = BitConverter.ToInt32(header, 12);
                if (magic != Magic || width <= 0 || height <= 0 || byteCount != width * height * 4)
                    throw new InvalidDataException("Invalid preview frame header.");

                var pixels = new byte[byteCount];
                await ReadExactAsync(pipe, pixels, cancellationToken);

                QueueFrame(width, height, pixels);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _dispatcher.TryEnqueue(() => ErrorReceived?.Invoke(ex.Message));
        }
    }

    private void QueueFrame(int width, int height, byte[] pixels)
    {
        var shouldQueue = false;
        lock (_frameLock)
        {
            _pendingWidth = width;
            _pendingHeight = height;
            _pendingPixels = pixels;
            if (!_applyQueued)
            {
                _applyQueued = true;
                shouldQueue = true;
            }
        }

        if (shouldQueue)
            _dispatcher.TryEnqueue(ApplyPendingFrame);
    }

    private void ApplyPendingFrame()
    {
        byte[]? pixels;
        int width;
        int height;
        lock (_frameLock)
        {
            pixels = _pendingPixels;
            width = _pendingWidth;
            height = _pendingHeight;
            _pendingPixels = null;
        }

        if (pixels == null)
        {
            lock (_frameLock)
                _applyQueued = false;
            return;
        }

        if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
            _bitmap = new WriteableBitmap(width, height);

        using (var stream = _bitmap.PixelBuffer.AsStream())
        {
            stream.Seek(0, SeekOrigin.Begin);
            stream.Write(pixels, 0, pixels.Length);
        }
        _bitmap.Invalidate();
        FrameReceived?.Invoke(_bitmap);

        var queueAgain = false;
        lock (_frameLock)
        {
            queueAgain = _pendingPixels != null;
            if (!queueAgain)
                _applyQueued = false;
        }

        if (queueAgain)
            _dispatcher.TryEnqueue(ApplyPendingFrame);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("Preview pipe closed.");
            offset += read;
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
