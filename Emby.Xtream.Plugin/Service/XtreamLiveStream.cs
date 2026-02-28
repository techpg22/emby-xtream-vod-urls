using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;

namespace Emby.Xtream.Plugin.Service
{
    internal sealed class XtreamLiveStream : ILiveStream, IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private HttpResponseMessage _response;
        private Stream _stream;
        private bool _disposed;

        public XtreamLiveStream(MediaSourceInfo mediaSource, string tunerHostId, HttpClient httpClient, ILogger logger = null)
        {
            MediaSource = mediaSource;
            _httpClient = httpClient;
            _logger = logger;
            UniqueId = Guid.NewGuid().ToString("N");
            TunerHostId = tunerHostId;
            OriginalStreamId = mediaSource.Id;
            DateOpened = DateTimeOffset.UtcNow;
        }

        public int ConsumerCount { get; set; }
        public string OriginalStreamId { get; set; }
        public string TunerHostId { get; }
        public bool EnableStreamSharing => false;
        public MediaSourceInfo MediaSource { get; set; }
        public string UniqueId { get; }
        public DateTimeOffset DateOpened { get; }
        public bool SupportsCopyTo => true;

        public async Task Open(CancellationToken openCancellationToken)
        {
            var sw = Stopwatch.StartNew();
            _response = await _httpClient.GetAsync(
                MediaSource.Path,
                HttpCompletionOption.ResponseHeadersRead,
                openCancellationToken).ConfigureAwait(false);
            _logger?.Info("[stream-timing] Open.HttpGet={0}ms status={1}", sw.ElapsedMilliseconds, (int)_response.StatusCode);
            sw.Restart();

            _response.EnsureSuccessStatusCode();
            _stream = await _response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            _logger?.Info("[stream-timing] Open.StreamReady={0}ms", sw.ElapsedMilliseconds);
        }

        public Task Close()
        {
            Dispose();
            return Task.CompletedTask;
        }

        public async Task CopyToAsync(PipeWriter writer, CancellationToken cancellationToken)
        {
            if (_stream == null)
                throw new InvalidOperationException("Stream not opened. Call Open() first.");

            var buffer = new byte[262144];
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int bytesRead = await _stream.ReadAsync(
                        buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);

                    if (bytesRead == 0) break;

                    var writeBuffer = writer.GetMemory(bytesRead);
                    buffer.AsMemory(0, bytesRead).CopyTo(writeBuffer);
                    writer.Advance(bytesRead);

                    var flushResult = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (flushResult.IsCompleted) break;
                }
            }
            finally
            {
                await writer.CompleteAsync().ConfigureAwait(false);
            }
        }

        public async Task CopyToAsync(
            Stream writer,
            DateTimeOffset? wallClockStartTime,
            Action<SegmentedStreamSegmentInfo> onSegmentWritten,
            CancellationToken cancellationToken)
        {
            if (_stream == null)
                throw new InvalidOperationException("Stream not opened. Call Open() first.");

            await _stream.CopyToAsync(writer, 262144, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _stream?.Dispose();
                _response?.Dispose();
                _httpClient?.Dispose();
                _disposed = true;
            }
        }
    }
}
