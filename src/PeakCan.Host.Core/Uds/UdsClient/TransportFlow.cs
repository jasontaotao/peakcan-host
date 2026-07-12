using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Uds.IsoTp;

namespace PeakCan.Host.Core.Uds;

public partial class UdsClient
{
    // Flow A: Transport wire layer + Rx + Dispose + test seam.
    // SendRequestAsync -> SendRequestInternalAsync -> OnMessageReceived + OnP2TimeoutFired.
    // Extracted from UdsClient.cs verbatim per W12 D5 (test-seam + Dispose grouped
    // with the OnMessageReceived subscription handler they pair with).
    //
    // Cross-flow callers (partial-class visible):
    //   - SendRequestAsync <-- all 13 UDS service methods (Flow B/C/D/E)

    /// <summary>
    /// Send a UDS service request and wait for response.
    /// </summary>
    /// <param name="serviceId">Service ID (SID).</param>
    /// <param name="data">Service data (excluding SID).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Response bytes (excluding SID + 0x40).</returns>
    /// <remarks>
    /// v1.2.14 PATCH Item 4: marked <c>virtual</c> so test doubles can
    /// intercept wire-level frame emit without subclassing the entire
    /// <see cref="UdsClient"/>. Visibility stays <c>public</c> for
    /// backwards compatibility with existing direct callers
    /// (e.g. <c>UdsClientTests</c>).
    /// </remarks>
    public virtual async Task<byte[]> SendRequestAsync(byte serviceId, byte[]? data = null, CancellationToken ct = default)
    {
        // Build request: SID + data
        byte[] request;
        if (data is null)
        {
            request = [serviceId];
        }
        else
        {
            request = new byte[1 + data.Length];
            request[0] = serviceId;
            Array.Copy(data, 0, request, 1, data.Length);
        }

        // Serialize requests to prevent overlapping
        await _requestLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SendRequestInternalAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public void Dispose()
    {
        _isoTp.MessageReceived -= OnMessageReceived;
        _requestLock.Dispose();
        Volatile.Read(ref _responseCts)?.Dispose();
        Session.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<byte[]> SendRequestInternalAsync(byte[] request, CancellationToken ct)
    {
        _pendingRequestSid = request[0];
        Volatile.Write(ref _responseTcs, new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously));
        Volatile.Write(ref _responseCts, CancellationTokenSource.CreateLinkedTokenSource(ct));

        // v1.2.13 PATCH Item 4: register a callback so P2 timeout unblocks
        // await _responseTcs.Task. Without this registration the linked CTS
        // cancel only fires OperationCanceledException for whoever awaits the
        // token directly - and nothing does. P2 timeout would silently hang
        // the caller. The callback TrySetCancels the TCS so the await resumes
        // with TaskCanceledException -> caught below -> rethrown as UdsException.
        var registration = _responseCts.Token.Register(
            static state => ((UdsClient)state!).OnP2TimeoutFired(), this);

        // Register timeout
        _responseCts.CancelAfter(_timer.P2Timeout);

        try
        {
            // Send via ISO-TP
            await _isoTp.SendMessageAsync(request, ct).ConfigureAwait(false);

            // Wait for response
            var response = await _responseTcs.Task.ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UdsException("UDS response timeout");
        }
        finally
        {
            // v1.2.13 PATCH Item 4 (Phase 2.5 new finding): strict ordering
            // matters here. Disposal sequence:
            //   1. registration.Dispose()  - unhook the Token.Register callback
            //                                so it cannot fire during/after Dispose
            //   2. Volatile.Write(_responseTcs, null) - OnMessageReceived sees no TCS
            //   3. Volatile.Write(_responseCts, null) - OnMessageReceived sees no CTS
            //   4. cts?.Dispose()           - last; no in-flight reference remains
            // Without this ordering OnMessageReceived may cts?.CancelAfter on a
            // disposed CTS (ObjectDisposedException propagates onto the SDK
            // read thread - process crash on graceful shutdown).
            registration.Dispose();
            var cts = Volatile.Read(ref _responseCts);
            Volatile.Write(ref _responseTcs, null);
            Volatile.Write(ref _responseCts, null);
            cts?.Dispose();
        }
    }

    private void OnP2TimeoutFired()
    {
        OnP2TimeoutFiredForTesting?.Invoke();
        Volatile.Read(ref _responseTcs)?.TrySetCanceled();
    }

    private void OnMessageReceived(byte[] data)
    {
        if (data.Length < 1)
            return;

        byte sid = data[0];

        // Item 14: acquire-load the pending response handles. Without
        // Volatile.Read the JIT may have cached or hoisted the read.
        var tcs = Volatile.Read(ref _responseTcs);
        var cts = Volatile.Read(ref _responseCts);

        // Check for negative response (0x7F)
        if (sid == 0x7F && data.Length >= 3)
        {
            byte requestedSid = data[1];
            byte nrc = data[2];

            // Handle NRC 0x78 (requestCorrectlyReceivedResponsePending)
            if (nrc == 0x78)
            {
                // v1.2.13 PATCH Item 4 (Phase 2.5): guard against disposed
                // CTS. After SendRequestInternalAsync's finally has nulled
                // the fields and disposed cts, a late-arriving response
                // (already in flight on the SDK read thread) would crash
                // here. The IsCancellationRequested check is the cheap
                // fast-path; the try/catch handles the disposed-after-read
                // race window.
                if (cts is not null && !cts.IsCancellationRequested)
                {
                    try { cts.CancelAfter(_timer.P2StarTimeout); }
                    catch (ObjectDisposedException) { /* shutdown race */ }
                }
                return;
            }

            // Other NRCs: complete with error
            tcs?.TrySetException(new UdsNegativeResponseException(requestedSid, (UdsNegativeResponseCode)nrc));
            return;
        }

        // Positive response: SID + 0x40
        if (data.Length >= 2)
        {
            // C-8 fix: validate the SID echoes our in-flight request's SID + 0x40.
            // A misaligned SID means the frame is stale or from a different
            // request; dropping it lets the P2 timer expire (semantically correct).
            byte expectedPositiveSid = (byte)(_pendingRequestSid + 0x40);
            if (sid != expectedPositiveSid)
                return;

            tcs?.TrySetResult(data[1..]);
        }
    }

    /// <summary>
    /// v1.2.13 PATCH Item 4: test-only public surface for OnMessageReceived.
    /// Allows tests to drive late-arriving-response paths without standing
    /// up an IsoTpLayer + ICanChannel.
    /// </summary>
    internal void PublicOnMessageReceivedForTesting(byte[] data) => OnMessageReceived(data);
}
