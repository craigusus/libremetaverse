using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LibreMetaverse.Http;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace LibreMetaverse.Tests
{
    /// <summary>
    /// The downloader's retry lifecycle. A retryable failure (5xx, transport exception, timeout
    /// exception, body read failure) once requeued the request onto its own still-registered,
    /// already-started entry, which was then removed: the retry never ran and every caller waited
    /// forever. Each retry must now really run, every caller must complete exactly once (success,
    /// the terminal failure, or cancellation), no attempt may run after completion, and no entry or
    /// queued request may remain. Every path is driven through a scripted handler whose URL carries
    /// fake secrets, which must never reach the log.
    /// </summary>
    [TestFixture]
    [Category("Http")]
    public class DownloadRetryTests
    {
        private const string Token = "RETRY-SECRET-TOKEN-91c0de";
        private const string Host = "retry-secret-host.invalid";
        private static readonly Uri SecretUri = new Uri($"https://{Host}:12043/cap/{Token}/?texture_id=RETRY-SECRET-QUERY");
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
        private static readonly byte[] Payload = Encoding.UTF8.GetBytes("asset bytes");

        private Capture _capture;
        private LogLevel _level;
        private readonly List<GridClient> _clients = new List<GridClient>();
        private readonly List<DownloadManager> _managers = new List<DownloadManager>();

        [SetUp]
        public void SetUp()
        {
            _level = Settings.LogLevel;
            Settings.LogLevel = LogLevel.Trace;
            _capture = new Capture();
            Logger.SetLoggerFactory(_capture, "LibreMetaverse.Tests.DownloadRetry");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var manager in _managers)
            {
                try { manager.Dispose(); } catch { }
            }
            _managers.Clear();
            foreach (var client in _clients)
            {
                try { client.HttpCapsClient?.Dispose(); } catch { }
            }
            _clients.Clear();
            Settings.LogLevel = _level;
            Logger.SetLoggerFactory(Logger.CreateDefaultConsoleLoggerFactory(), typeof(Logger).FullName ?? "LibreMetaverse");
            _capture.Dispose();
        }

        // ---- A retry that then succeeds ---------------------------------------------------------------

        [Test]
        public Task ServerErrorThenSuccess() => RetriedThenSucceeds(_ => Status(HttpStatusCode.ServiceUnavailable), "status=503; retry=1/3");

        [Test]
        public Task TransportFailureThenSuccess() =>
            RetriedThenSucceeds(_ => throw new HttpRequestException($"Connection reset for {SecretUri}"), "exception=HttpRequestException");

        [Test]
        public Task TimeoutThenSuccess() =>
            RetriedThenSucceeds(_ => throw new TimeoutException($"Timed out on {SecretUri}"), "exception=TimeoutException");

        [Test]
        public Task BodyReadFailureThenSuccess() =>
            RetriedThenSucceeds(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ThrowingStream($"Reset {SecretUri}")) },
                "status=200; retry=1/3");

        private async Task RetriedThenSucceeds(Func<HttpRequestMessage, HttpResponseMessage> first, string retryLog)
        {
            var handler = new Scripted((call, request) => call == 1 ? first(request) : Ok());
            var manager = Manager(handler);
            var (response, data) = await Within(manager.QueueDownloadAsync(SecretUri, null, null, CancellationToken.None, retries: 3)).ConfigureAwait(false);
            Assert.That(response.IsSuccessStatusCode, Is.True);
            Assert.That(data, Is.EqualTo(Payload));
            Assert.That(handler.Calls, Is.EqualTo(2), "The retry did not run exactly once");
            await Settled(manager, handler, expectedCalls: 2).ConfigureAwait(false);
            AssertLogged(retryLog);
            AssertLogged("Asset download finished; status=200; attempts=1; bytes=" + Payload.Length + "; exception=none");
            AssertNoSecrets();
        }

        // ---- Retries exhausted ------------------------------------------------------------------------

        [Test]
        public Task RepeatedServerErrors() =>
            Exhausted(_ => Status(HttpStatusCode.BadGateway), typeof(HttpRequestException), "Asset download finished; status=502; attempts=2");

        [Test]
        public Task RepeatedTransportFailures() =>
            Exhausted(_ => throw new HttpRequestException($"Refused {SecretUri}"), typeof(HttpRequestException), "Asset download exception; retry=2/2");

        [Test]
        public Task RepeatedTimeouts() =>
            Exhausted(_ => throw new TimeoutException($"Timed out {SecretUri}"), typeof(TimeoutException), "Asset download exception; retry=2/2");

        private async Task Exhausted(Func<HttpRequestMessage, HttpResponseMessage> respond, Type error, string log)
        {
            var handler = new Scripted((_, request) => respond(request));
            var manager = Manager(handler);
            var task = manager.QueueDownloadAsync(SecretUri, null, null, CancellationToken.None, retries: 2);
            var failure = await Failure(task).ConfigureAwait(false);
            Assert.That(failure, Is.InstanceOf(error));
            Assert.That(handler.Calls, Is.EqualTo(3), "Not exactly the first attempt and two retries");
            await Settled(manager, handler, expectedCalls: 3).ConfigureAwait(false);
            AssertLogged(log);
            AssertNoSecrets();
        }

        // ---- Permanent failure ------------------------------------------------------------------------

        [Test]
        public async Task NotFoundIsTerminal()
        {
            var handler = new Scripted((_, __) => Status(HttpStatusCode.NotFound));
            var manager = Manager(handler);
            var failure = await Failure(manager.QueueDownloadAsync(SecretUri, null, null, CancellationToken.None, retries: 5)).ConfigureAwait(false);
            Assert.That(failure, Is.InstanceOf<HttpRequestException>());
            await Settled(manager, handler, expectedCalls: 1).ConfigureAwait(false);
            AssertNoSecrets();
        }

        // ---- Cancellation -----------------------------------------------------------------------------

        [Test]
        public async Task CancelDuringRequest()
        {
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new Scripted((_, __, token) =>
            {
                started.TrySetResult(true);
                return Task.Delay(Timeout.Infinite, token).ContinueWith<HttpResponseMessage>(_ => throw new OperationCanceledException(token),
                    TaskScheduler.Default);
            });
            var manager = Manager(handler);
            using (var cancel = new CancellationTokenSource())
            {
                var task = manager.QueueDownloadAsync(SecretUri, null, null, cancel.Token, retries: 3);
                await Within(started.Task).ConfigureAwait(false);
                cancel.Cancel();
                var failure = await Failure(task).ConfigureAwait(false);
                Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
            }
            await Settled(manager, handler, expectedCalls: 1).ConfigureAwait(false);
            AssertNoSecrets(expectLines: false);
        }

        [Test]
        public async Task CancelBetweenRetries()
        {
            var handler = new Scripted((_, __) => Status(HttpStatusCode.ServiceUnavailable));
            var manager = Manager(handler);
            using (var cancel = new CancellationTokenSource())
            {
                var task = manager.QueueDownloadAsync(SecretUri, null, null, cancel.Token, retries: 3);
                await Until(() => _capture.Lines.Any(l => l.IndexOf("retry=1/3", StringComparison.Ordinal) >= 0)).ConfigureAwait(false);
                cancel.Cancel(); // During the backoff before the first retry.
                var failure = await Failure(task).ConfigureAwait(false);
                Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
            }
            await Settled(manager, handler, expectedCalls: 1).ConfigureAwait(false);
            AssertNoSecrets();
        }

        // ---- Coalesced callers ------------------------------------------------------------------------

        [Test]
        public async Task TwoCallersThroughARetryBothComplete()
        {
            var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new Scripted(async (call, _, __) =>
            {
                if (call != 1) return Ok();
                firstStarted.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return Status(HttpStatusCode.ServiceUnavailable);
            });
            var manager = Manager(handler);
            var first = manager.QueueDownloadAsync(SecretUri, null, null, CancellationToken.None, retries: 3);
            await Within(firstStarted.Task).ConfigureAwait(false);
            var second = manager.QueueDownloadAsync(SecretUri, null, null, CancellationToken.None, retries: 3); // Joins the same download.
            release.SetResult(true);
            var a = await Within(first).ConfigureAwait(false);
            var b = await Within(second).ConfigureAwait(false);
            Assert.That(a.data, Is.EqualTo(Payload));
            Assert.That(b.data, Is.EqualTo(Payload));
            await Settled(manager, handler, expectedCalls: 2).ConfigureAwait(false);
            AssertNoSecrets();
        }

        [Test]
        public async Task AFailedDownloadDoesNotPoisonALaterOne()
        {
            var fail = true;
            var handler = new Scripted((_, __) => Volatile.Read(ref fail) ? Status(HttpStatusCode.NotFound) : Ok());
            var manager = Manager(handler);
            await Failure(manager.QueueDownloadAsync(SecretUri, null, null, CancellationToken.None, retries: 2)).ConfigureAwait(false);
            await Settled(manager, handler, expectedCalls: 1).ConfigureAwait(false);
            Volatile.Write(ref fail, false);
            var (_, data) = await Within(manager.QueueDownloadAsync(SecretUri, null, null, CancellationToken.None, retries: 2)).ConfigureAwait(false);
            Assert.That(data, Is.EqualTo(Payload));
            await Settled(manager, handler, expectedCalls: 2).ConfigureAwait(false);
            AssertNoSecrets();
        }

        [Test]
        public async Task SuccessLeavesNothingBehind()
        {
            var handler = new Scripted((_, __) => Ok());
            var manager = Manager(handler);
            var (_, data) = await Within(manager.QueueDownloadAsync(SecretUri, null, null, CancellationToken.None, retries: 2)).ConfigureAwait(false);
            Assert.That(data, Is.EqualTo(Payload));
            await Settled(manager, handler, expectedCalls: 1).ConfigureAwait(false);
            AssertNoSecrets();
        }

        // ---- Helpers ----------------------------------------------------------------------------------

        // No entry or queued request remains, and no further attempt runs afterwards (longer than the
        // largest backoff, 2.2 s).
        private static async Task Settled(DownloadManager manager, Scripted handler, int expectedCalls)
        {
            await Until(() => manager.ActiveCount == 0 && manager.QueuedCount == 0).ConfigureAwait(false);
            await Task.Delay(2500).ConfigureAwait(false);
            Assert.That(handler.Calls, Is.EqualTo(expectedCalls), "An attempt ran after the download completed");
            Assert.That(manager.ActiveCount, Is.EqualTo(0), "A download entry remained");
            Assert.That(manager.QueuedCount, Is.EqualTo(0), "A queued request remained");
        }

        private DownloadManager Manager(HttpMessageHandler handler)
        {
            var client = new GridClient();
            try { client.HttpCapsClient?.Dispose(); } catch { }
            client.HttpCapsClient = new HttpCapsClient(handler);
            _clients.Add(client);
            var manager = new DownloadManager(client);
            _managers.Add(manager);
            return manager;
        }

        private static async Task<T> Within<T>(Task<T> task)
        {
            var finished = await Task.WhenAny(task, Task.Delay(Bound)).ConfigureAwait(false);
            Assert.That(finished, Is.SameAs(task), "The caller's task did not complete");
            return await task.ConfigureAwait(false);
        }

        private static async Task<Exception> Failure<T>(Task<T> task)
        {
            var finished = await Task.WhenAny(task, Task.Delay(Bound)).ConfigureAwait(false);
            Assert.That(finished, Is.SameAs(task), "The caller's task did not complete");
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                return error;
            }
            Assert.Fail("The download did not fail");
            return null;
        }

        private static async Task Until(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + Bound;
            while (!condition())
            {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "Condition not reached in time");
                await Task.Delay(10).ConfigureAwait(false);
            }
        }

        private void AssertLogged(string fragment) =>
            Assert.That(_capture.Lines.Any(l => l.IndexOf(fragment, StringComparison.Ordinal) >= 0), Is.True,
                "Missing diagnostic '" + fragment + "' in:\n" + string.Join("\n", _capture.Lines));

        private void AssertNoSecrets(bool expectLines = true)
        {
            var lines = _capture.Lines.ToArray();
            if (expectLines)
                Assert.That(lines, Is.Not.Empty, "Nothing was logged");
            foreach (var line in lines)
            {
                foreach (var secret in new[] { Token, Host, "RETRY-SECRET-QUERY", "/cap/" })
                    Assert.That(line.IndexOf(secret, StringComparison.OrdinalIgnoreCase), Is.LessThan(0), "Logged a secret: " + line);
            }
        }

        private static HttpResponseMessage Ok()
        {
            var content = new ByteArrayContent(Payload);
            content.Headers.ContentLength = Payload.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        private static HttpResponseMessage Status(HttpStatusCode status)
        {
            var content = new ByteArrayContent(new byte[0]);
            content.Headers.ContentLength = 0;
            return new HttpResponseMessage(status) { Content = content, ReasonPhrase = "Reason " + Token };
        }

        private sealed class Scripted : HttpMessageHandler
        {
            private readonly Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
            private int _calls;

            public Scripted(Func<int, HttpRequestMessage, HttpResponseMessage> respond) =>
                _respond = (call, request, _) => Task.FromResult(respond(call, request));

            public Scripted(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

            public int Calls => Volatile.Read(ref _calls);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var call = Interlocked.Increment(ref _calls);
                var response = await _respond(call, request, cancellationToken).ConfigureAwait(false);
                response.RequestMessage = request;
                return response;
            }
        }

        private sealed class ThrowingStream : Stream
        {
            private readonly string _message;
            public ThrowingStream(string message) => _message = message;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new IOException(_message);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                throw new IOException(_message);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class Capture : ILoggerFactory, ILogger
        {
            private readonly ConcurrentQueue<string> _lines = new ConcurrentQueue<string>();
            public IReadOnlyList<string> Lines => _lines.ToArray();
            public ILogger CreateLogger(string categoryName) => this;
            public void AddProvider(ILoggerProvider provider) { }
            public void Dispose() { }
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) =>
                _lines.Enqueue(logLevel + ": " + formatter(state, exception) + (exception == null ? "" : " | " + exception));
        }
    }
}
