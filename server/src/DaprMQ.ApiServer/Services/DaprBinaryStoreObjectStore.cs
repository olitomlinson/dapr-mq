using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using Dapr;
using Dapr.BinaryStore;
using DaprMQ.Interfaces;
using Grpc.Core;

namespace DaprMQ.ApiServer.Services;

/// <summary>
/// IObjectStore implementation backed by a Dapr Binary Store component (binarystore.azure.blobstorage,
/// binarystore.aws.s3, ...). Uploads and downloads stream in chunks; nothing is buffered whole.
/// </summary>
public class DaprBinaryStoreObjectStore : IObjectStore
{
    private static readonly Regex ValidPrefixPattern = new("^[A-Za-z0-9_-]+(/[A-Za-z0-9_-]+)*$", RegexOptions.Compiled);

    private readonly DaprBinaryStoreClient _client;
    private readonly ObjectStoreConfig _config;

    public DaprBinaryStoreObjectStore(DaprBinaryStoreClient client, ObjectStoreConfig config)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public async Task<string> UploadAsync(string userPrefix, string objectId, Stream content, string? contentType, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userPrefix) || !ValidPrefixPattern.IsMatch(userPrefix))
        {
            throw new ArgumentException($"Invalid prefix '{userPrefix}'", nameof(userPrefix));
        }

        var key = $"{userPrefix}/{objectId}";

        // The SDK waits for the call's response when a source read fails, and the runtime waits for more
        // chunks, so a failed read (client gone, body over the size cap) must cancel the call itself.
        // The read failure, not the resulting cancellation, is what the caller sees.
        using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var source = new CancelOnReadFailureStream(content, uploadCts);
        try
        {
            await _client.SetBinaryFileAsync(_config.StoreName, key, source, overwrite: false, cancellationToken: uploadCts.Token);
        }
        catch (Exception) when (source.ReadFailure != null)
        {
            ExceptionDispatchInfo.Throw(source.ReadFailure);
        }
        catch (DaprException ex) when (StatusOf(ex) is { StatusCode: StatusCode.ResourceExhausted } status)
        {
            // The runtime sends both "file too large" (413) and "store full" (507) as ResourceExhausted,
            // with only the message to tell them apart.
            throw status.Detail.Contains("store is full", StringComparison.OrdinalIgnoreCase)
                ? new ObjectStoreFullException(key, ex)
                : new ObjectTooLargeException(key, ex);
        }

        return key;
    }

    public async Task<Stream> DownloadAsync(string blobReference, CancellationToken cancellationToken = default)
    {
        var chunks = _client.GetBinaryFileChunksAsync(_config.StoreName, blobReference, cancellationToken).GetAsyncEnumerator(cancellationToken);
        bool hasChunk;
        try
        {
            // Read the first chunk now, so a missing object fails before the caller starts a response.
            hasChunk = await chunks.MoveNextAsync();
        }
        catch (Exception ex)
        {
            await chunks.DisposeAsync();
            if (ex is DaprException daprException && StatusOf(daprException)?.StatusCode == StatusCode.NotFound)
            {
                throw new ObjectNotFoundException(blobReference, ex);
            }

            throw;
        }

        return new ChunkReadStream(chunks, hasChunk);
    }

    public async Task DeleteAsync(string blobReference, CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.DeleteBinaryFileAsync(_config.StoreName, blobReference, cancellationToken);
        }
        catch (DaprException ex) when (StatusOf(ex)?.StatusCode == StatusCode.NotFound)
        {
            // Already gone: the delete's goal is met.
        }
    }

    private static Status? StatusOf(DaprException ex) => (ex.InnerException as RpcException)?.Status;

    /// <summary>Pass-through that records the first read failure and cancels the upload.</summary>
    private sealed class CancelOnReadFailureStream(Stream inner, CancellationTokenSource uploadCts) : ReadOnlyStream
    {
        public Exception? ReadFailure { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                return await inner.ReadAsync(buffer, cancellationToken);
            }
            catch (Exception ex) when (!uploadCts.IsCancellationRequested)
            {
                ReadFailure = ex;
                await uploadCts.CancelAsync();
                throw;
            }
        }
    }

    /// <summary>Stream over the runtime's chunks. Disposing it ends the download call.</summary>
    private sealed class ChunkReadStream(IAsyncEnumerator<ReadOnlyMemory<byte>> chunks, bool hasChunk) : ReadOnlyStream
    {
        private ReadOnlyMemory<byte> _current = hasChunk ? chunks.Current : ReadOnlyMemory<byte>.Empty;
        private bool _ended = !hasChunk;
        private bool _disposed;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_current.IsEmpty && !_ended)
            {
                if (await chunks.MoveNextAsync())
                {
                    _current = chunks.Current;
                }
                else
                {
                    _ended = true;
                }
            }

            var count = Math.Min(buffer.Length, _current.Length);
            _current[..count].CopyTo(buffer);
            _current = _current[count..];
            return count;
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                await chunks.DisposeAsync();
            }

            await base.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                chunks.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Forward-only, read-only stream; subclasses implement the Memory-based ReadAsync.</summary>
    private abstract class ReadOnlyStream : Stream
    {
        public abstract override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
