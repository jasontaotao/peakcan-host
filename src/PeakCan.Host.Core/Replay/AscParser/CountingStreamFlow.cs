namespace PeakCan.Host.Core.Replay;

public static partial class AscParser
{
    // Flow C: CountingStream nested class methods (v3.10.0 MINOR T4 (H5) + earlier).
    // Read-only wrapper that counts cumulative bytes read from _inner and throws
    // ReplayLoadException the moment count exceeds _maxBytes. Stream-size cap.
    //
    // The class declaration + 3 fields (_inner, _maxBytes, _count) stay in
    // main per W10 D5 + W9 D6 sister lessons (nested-class-declaration-stays-
    // with-outer-class + state-ownership). Methods move to this partial file.

    private sealed partial class CountingStream : Stream
    {
        public CountingStream(Stream inner, long maxBytes)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _maxBytes = maxBytes;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            Accumulate(n);
            return n;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Accumulate(n);
            return n;
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            // Only count after the read completes so we don't accumulate
            // bytes that ended up not delivered (rare for forward-only
            // pipes, but the API contract is "bytes transferred").
            return AwaitAndCount(_inner.ReadAsync(buffer, offset, count, cancellationToken));
        }

        private async Task<int> AwaitAndCount(Task<int> task)
        {
            var n = await task.ConfigureAwait(false);
            Accumulate(n);
            return n;
        }

        private void Accumulate(int bytesRead)
        {
            if (bytesRead <= 0) return;
            _count += bytesRead;
            if (_count > _maxBytes)
            {
                throw new ReplayLoadException(
                    $"ASC stream exceeds size cap ({_count:N0} > {_maxBytes:N0} bytes)");
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
