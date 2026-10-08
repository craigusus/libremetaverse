using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LibreMetaverse.Http;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace LibreMetaverse.Tests
{
    /// <summary>
    /// The HTTP asset downloader (DownloadManager, and the asset requests that use it) must never log a
    /// request address: for an asset it is a ViewerAsset capability URL, a session secret. Every
    /// failure, retry and completion path is driven through a scripted handler whose URL carries
    /// unmistakable fake secrets; the captured log output (messages and any attached exceptions, at
    /// every level) must contain none of them, while still saying what happened.
    /// </summary>
    [TestFixture]
    [Category("Http")]
    public class DownloadSecretLoggingTests
    {
        private const string Token = "SECRET-CAP-TOKEN-7f3a91c4";
        private const string Query = "SECRET-QUERY-55d2e0b8";
        private const string Host = "caps-secret-host.invalid";
        private static readonly Uri SecretUri = new Uri($"https://{Host}:12043/cap/{Token}/?texture_id={Query}");
        private static readonly Uri SeedUri = new Uri($"https://{Host}:12043/cap/SEED-{Token}");
        private static readonly string ViewerAssetCap = $"https://{Host}:12043/cap/VIEWERASSET-{Token}";
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

        private CaptureFactory _capture;
        private LogLevel _level;
        private readonly List<GridClient> _clients = new List<GridClient>();
        private readonly List<DownloadManager> _downloads = new List<DownloadManager>();

        [SetUp]
        public void SetUp()
        {
            _level = Settings.LogLevel;
            Settings.LogLevel = LogLevel.Trace; // Every level the downloader uses, Trace included.
            _capture = new CaptureFactory();
            Logger.SetLoggerFactory(_capture, "LibreMetaverse.Tests.DownloadSecretLogging");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var downloads in _downloads)
            {
                try { downloads.Dispose(); } catch { }
            }
            _downloads.Clear();
            foreach (var client in _clients)
            {
                try { client.Network.Shutdown(NetworkManager.DisconnectType.ClientInitiated, "DownloadSecretLoggingTests"); } catch { }
                try { client.HttpCapsClient?.Dispose(); } catch { }
            }
            _clients.Clear();
            Settings.LogLevel = _level;
            Logger.SetLoggerFactory(Logger.CreateDefaultConsoleLoggerFactory(), typeof(Logger).FullName ?? "LibreMetaverse");
            _capture.Dispose();
        }

        // ---- The downloader ------------------------------------------------------------------------
        // A retryable failure logs its retry, then (an existing downloader behaviour this logging-only
        // change leaves alone) the requeued attempt never runs and the caller's task never completes;
        // so retry paths are checked by their log lines, not by completion.

        [Test]
        public async Task RetryableServerError()
        {
            var handler = new Scripted(_ => Status(HttpStatusCode.ServiceUnavailable));
            Start(handler, retries: 2);
            await LoggedAsync("Asset download failed; status=503; retry=1/2").ConfigureAwait(false);
            await LoggedAsync("Asset download failed; status=503; attempts=1; exception=HttpRequestException").ConfigureAwait(false);
            AssertNoSecrets();
        }

        [Test]
        public async Task ServerErrorWithRetriesExhausted()
        {
            // The final failure: completes with the status error (whose reason phrase holds the token) unlogged.
            var handler = new Scripted(_ => Status(HttpStatusCode.ServiceUnavailable));
            var error = await DownloadFails(handler, retries: 0).ConfigureAwait(false);
            Assert.That(error, Is.InstanceOf<HttpRequestException>());
            Assert.That(error.Message, Does.Contain(Token), "The test's status error should carry the secret");
            Assert.That(handler.Calls, Is.EqualTo(1));
            AssertNoSecrets();
            AssertLogged("Asset download finished; status=503; attempts=0; bytes=0; exception=HttpRequestException");
        }

        [Test]
        public async Task TransportExceptionCarryingTheAddress()
        {
            // An exception whose message holds the full address must not reach the log through its text.
            var handler = new Scripted(_ => throw new HttpRequestException($"Connection reset while requesting {SecretUri}"));
            Start(handler, retries: 2);
            await LoggedAsync("Asset download exception; retry=1/2; exception=HttpRequestException").ConfigureAwait(false);
            await LoggedAsync("Asset download exception; attempts=1; exception=HttpRequestException").ConfigureAwait(false);
            AssertNoSecrets();
        }

        [Test]
        public async Task TimeoutException()
        {
            var handler = new Scripted(_ => throw new TimeoutException($"The operation on {SecretUri} timed out"));
            Start(handler, retries: 1);
            await LoggedAsync("Asset download exception; retry=1/1; exception=TimeoutException").ConfigureAwait(false);
            await LoggedAsync("Asset download exception; attempts=1; exception=TimeoutException").ConfigureAwait(false);
            AssertNoSecrets();
        }

        [Test]
        public async Task HttpClientTimeoutCancellation()
        {
            // HttpClient's own timeout surfaces as a TaskCanceledException: the cancellation path.
            var handler = new Scripted(_ => throw new TaskCanceledException(
                $"The request to {SecretUri} was canceled due to the configured HttpClient.Timeout", new TimeoutException(SecretUri.ToString())));
            var error = await DownloadFails(handler, retries: 2).ConfigureAwait(false);
            Assert.That(error, Is.InstanceOf<OperationCanceledException>());
            AssertNoSecrets(expectLines: false); // The cancellation path logs nothing; it must not log the address either.
        }

        [Test]
        public async Task BodyReadFailureCarryingTheAddress()
        {
            var handler = new Scripted(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ThrowingStream($"Stream reset for {SecretUri}")) });
            Start(handler, retries: 1);
            await LoggedAsync("Asset download failed; status=200; retry=1/1").ConfigureAwait(false);
            await LoggedAsync("Asset download failed; status=200; attempts=1; exception=IOException").ConfigureAwait(false);
            AssertNoSecrets();
        }

        [Test]
        public async Task PermanentNotFound()
        {
            var handler = new Scripted(_ => Status(HttpStatusCode.NotFound, "cap not found: " + Token));
            var error = await DownloadFails(handler, retries: 3).ConfigureAwait(false);
            Assert.That(error, Is.InstanceOf<HttpRequestException>());
            Assert.That(handler.Calls, Is.EqualTo(1));
            AssertNoSecrets();
            AssertLogged("Asset download finished; status=404; attempts=0; bytes=");
        }

        [Test]
        public async Task SuccessLogsNoAddress()
        {
            var handler = new Scripted(_ => Status(HttpStatusCode.OK, "fine"));
            await Download(handler, retries: 1).ConfigureAwait(false);
            AssertNoSecrets();
            AssertLogged("Asset download finished; status=200; attempts=0; bytes=4; exception=none");
        }

        // ---- The asset requests over ViewerAsset (the landmark "Load destination" path) -------------

        [Test]
        public async Task InventoryLandmarkAssetServerError()
        {
            var client = SeededClient(_ => Status(HttpStatusCode.ServiceUnavailable));
            _ = client.Assets.RequestInventoryAssetAsync(UUID.Random(), UUID.Random(), UUID.Zero, UUID.Random(),
                AssetType.Landmark, true, UUID.Random(), CancellationToken.None);
            await LoggedAsync("Asset download failed; status=503; retry=1/5").ConfigureAwait(false);
            await LoggedAsync("Asset download failed; status=503; attempts=1; exception=HttpRequestException").ConfigureAwait(false);
            AssertNoSecrets();
        }

        [Test]
        public async Task InventoryLandmarkAssetTransportException()
        {
            var client = SeededClient(request => throw new HttpRequestException($"Socket error for {request.RequestUri}"));
            _ = client.Assets.RequestInventoryAssetAsync(UUID.Random(), UUID.Random(), UUID.Zero, UUID.Random(),
                AssetType.Landmark, true, UUID.Random(), CancellationToken.None);
            await LoggedAsync("Asset download exception; retry=1/5; exception=HttpRequestException").ConfigureAwait(false);
            await LoggedAsync("Asset download exception; attempts=1; exception=HttpRequestException").ConfigureAwait(false);
            AssertNoSecrets();
        }

        [Test]
        public async Task InventoryLandmarkAssetPermanentFailure()
        {
            // The asset request's own failure line: the status error's text (it carries the token) is not logged.
            var client = SeededClient(_ => Status(HttpStatusCode.NotFound, "cap not found: " + Token));
            var asset = await Within(client.Assets.RequestInventoryAssetAsync(UUID.Random(), UUID.Random(), UUID.Zero, UUID.Random(),
                AssetType.Landmark, true, UUID.Random(), CancellationToken.None)).ConfigureAwait(false);
            Assert.That(asset, Is.Null);
            AssertNoSecrets();
            AssertLogged("; exception=HttpRequestException");
            AssertLogged("Failed to fetch asset ");
        }

        [Test]
        public async Task AssetPermanentFailure()
        {
            var client = SeededClient(_ => Status(HttpStatusCode.Forbidden));
            var asset = await Within(client.Assets.RequestAssetAsync(UUID.Random(), AssetType.Landmark, true, CancellationToken.None)).ConfigureAwait(false);
            Assert.That(asset, Is.Null);
            AssertNoSecrets();
            AssertLogged("Asset download finished; status=403");
            AssertLogged("Failed to fetch asset ");
        }

        [Test]
        public async Task InventoryLandmarkAssetSuccess()
        {
            var body = "Landmark version 2\nregion_id 7e7e7e7e-0000-4000-8000-0000000000a1\nlocal_pos 128.00 64.50 22.00\n";
            var client = SeededClient(_ => Status(HttpStatusCode.OK, body));
            var asset = await Within(client.Assets.RequestInventoryAssetAsync(UUID.Random(), UUID.Random(), UUID.Zero, UUID.Random(),
                AssetType.Landmark, true, UUID.Random(), CancellationToken.None)).ConfigureAwait(false);
            Assert.That(asset, Is.Not.Null);
            Assert.That(Encoding.UTF8.GetString(asset.AssetData), Is.EqualTo(body));
            AssertNoSecrets();
            AssertLogged("Asset download finished; status=200");
            // The asset's contents are never logged either.
            Assert.That(_capture.Lines.Any(l => l.IndexOf("region_id", StringComparison.Ordinal) >= 0), Is.False);
        }

        // ---- The source: no downloader log statement prints a request address or exception text ----

        [Test]
        public void DownloaderLogStatementsCarryNoAddressOrExceptionText()
        {
            var root = SourceRoot();
            var downloader = File.ReadAllText(Path.Combine(root, "LibreMetaverse", "DownloadManager.cs"));
            var assets = File.ReadAllText(Path.Combine(root, "LibreMetaverse", "AssetManager.cs"));
            var statements = LogStatements(downloader)
                .Concat(LogStatements(Method(assets, "private async Task RequestAssetHTTP(")))
                .Concat(LogStatements(Method(assets, "private async Task RequestInventoryAssetHTTP(")))
                .ToList();
            Assert.That(statements.Count, Is.GreaterThanOrEqualTo(7), "Log statements not found");
            foreach (var statement in statements)
            {
                Assert.That(Regex.IsMatch(statement, @"\baddr\b|Address|AbsoluteUri|RequestUri|\buri\b|\bUri\b|\breq\b|\{ex\}|\{ex\.Message\}|\.Message\b|\.ToString\(\)|ex\s*[,)]|BuildFetchRequestUri"),
                    Is.False, "A downloader log statement can print the request address or exception text: " + statement);
            }
        }

        // ---- Helpers ----------------------------------------------------------------------------------

        private void AssertNoSecrets(bool expectLines = true)
        {
            var lines = _capture.Lines.ToArray();
            if (expectLines)
                Assert.That(lines, Is.Not.Empty, "Nothing was logged");
            foreach (var line in lines)
            {
                foreach (var secret in new[] { Token, Query, Host, SecretUri.AbsoluteUri, SecretUri.AbsolutePath, "/cap/", ViewerAssetCap })
                    Assert.That(line.IndexOf(secret, StringComparison.OrdinalIgnoreCase), Is.LessThan(0), "Logged a secret: " + line);
            }
        }

        private void AssertLogged(string fragment)
        {
            Assert.That(_capture.Lines.Any(l => l.IndexOf(fragment, StringComparison.Ordinal) >= 0), Is.True,
                "Missing diagnostic '" + fragment + "' in:\n" + string.Join("\n", _capture.Lines));
        }

        // Queues a download whose task is not awaited (a retry path never completes it).
        private void Start(Scripted handler, int retries)
        {
            var downloads = new DownloadManager(NewClient(handler));
            _downloads.Add(downloads);
            _ = downloads.QueueDownloadAsync(SecretUri, null, null, CancellationToken.None, retries: retries);
        }

        private async Task LoggedAsync(string fragment)
        {
            var deadline = DateTime.UtcNow + Bound;
            while (!_capture.Lines.Any(l => l.IndexOf(fragment, StringComparison.Ordinal) >= 0))
            {
                if (DateTime.UtcNow > deadline)
                    Assert.Fail("Missing diagnostic '" + fragment + "' in:\n" + string.Join("\n", _capture.Lines));
                await Task.Delay(20).ConfigureAwait(false);
            }
        }

        private async Task<(HttpResponseMessage response, byte[] data)> Download(Scripted handler, int retries)
        {
            var client = NewClient(handler);
            using (var downloads = new DownloadManager(client))
            {
                var task = downloads.QueueDownloadAsync(SecretUri, null, null, CancellationToken.None, retries: retries);
                var finished = await Task.WhenAny(task, Task.Delay(Bound)).ConfigureAwait(false);
                Assert.That(finished, Is.SameAs(task), "The download did not finish in time");
                return await task.ConfigureAwait(false);
            }
        }

        private static async Task<T> Within<T>(Task<T> task)
        {
            var finished = await Task.WhenAny(task, Task.Delay(Bound)).ConfigureAwait(false);
            Assert.That(finished, Is.SameAs(task), "The asset request did not finish in time");
            return await task.ConfigureAwait(false);
        }

        private async Task<Exception> DownloadFails(Scripted handler, int retries)
        {
            try
            {
                await Download(handler, retries).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                return error;
            }
            Assert.Fail("The download did not fail");
            return null;
        }

        private GridClient NewClient(HttpMessageHandler handler)
        {
            var client = new GridClient();
            try { client.HttpCapsClient?.Dispose(); } catch { }
            client.HttpCapsClient = new HttpCapsClient(handler);
            _clients.Add(client);
            return client;
        }

        // A client whose current simulator's capabilities (from a scripted seed) include a ViewerAsset
        // capability URL carrying the fake secret; every other request goes to asset.
        private GridClient SeededClient(Func<HttpRequestMessage, HttpResponseMessage> asset)
        {
            var handler = new Scripted((call, request) =>
            {
                if (request.RequestUri == SeedUri)
                {
                    return Status(HttpStatusCode.OK,
                        "<?xml version=\"1.0\"?><llsd><map><key>ViewerAsset</key><string>" + ViewerAssetCap + "</string></map></llsd>",
                        "application/llsd+xml");
                }
                return asset(request);
            });
            var client = NewClient(handler);
            client.Settings.AssetCache.Enabled = false; // Always the HTTP path.
            typeof(NetworkManager).GetProperty(nameof(NetworkManager.Connected))
                .GetSetMethod(true).Invoke(client.Network, new object[] { true });
            var simulator = new Simulator(client, new IPEndPoint(IPAddress.Loopback, 0), 0);
            client.Network.CurrentSim = simulator;
            simulator.SetSeedCaps(SeedUri, changedSim: true);
            var deadline = DateTime.UtcNow + Bound;
            while (simulator.Caps?.CapabilityURI("ViewerAsset") == null)
            {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "The scripted seed was not applied");
                Thread.Sleep(10);
            }
            return client;
        }

        private static HttpResponseMessage Status(HttpStatusCode status, string body = "", string type = "application/octet-stream")
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            content.Headers.ContentLength = Encoding.UTF8.GetByteCount(body);
            return new HttpResponseMessage(status) { Content = content, ReasonPhrase = "Reason " + Token };
        }

        // Every line that calls the logger (each such statement is one line), comments removed.
        private static IEnumerable<string> LogStatements(string source) =>
            source.Split('\n').Select(line => Regex.Replace(line, @"^\s*//.*$", ""))
                .Where(line => Regex.IsMatch(line, @"Logger\.\w+\("));

        private static string Method(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), "Method not found: " + signature);
            var end = source.IndexOf("\n        private ", start + signature.Length, StringComparison.Ordinal);
            var next = source.IndexOf("\n        public ", start + signature.Length, StringComparison.Ordinal);
            if (end < 0 || (next >= 0 && next < end)) end = next;
            return end < 0 ? source.Substring(start) : source.Substring(start, end - start);
        }

        private static string SourceRoot()
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LibreMetaverse", "DownloadManager.cs")))
                dir = dir.Parent;
            Assert.That(dir, Is.Not.Null, "Source root not found");
            return dir.FullName;
        }

        private sealed class Scripted : HttpMessageHandler
        {
            private readonly Func<int, HttpRequestMessage, HttpResponseMessage> _respond;
            private int _calls;

            public Scripted(Func<int, HttpResponseMessage> respond) => _respond = (call, _) => respond(call);
            public Scripted(Func<int, HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

            public int Calls => Volatile.Read(ref _calls);

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var call = Interlocked.Increment(ref _calls);
                var response = _respond(call, request);
                response.RequestMessage = request;
                return Task.FromResult(response);
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

        // Records every log entry as its formatted message plus the full text of any attached exception.
        private sealed class CaptureFactory : ILoggerFactory
        {
            public readonly ConcurrentQueue<string> Lines = new ConcurrentQueue<string>();
            public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);
            public void AddProvider(ILoggerProvider provider) { }
            public void Dispose() { }
        }

        private sealed class CaptureLogger : ILogger
        {
            private readonly CaptureFactory _factory;
            public CaptureLogger(CaptureFactory factory) => _factory = factory;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) =>
                _factory.Lines.Enqueue(logLevel + ": " + formatter(state, exception) + (exception == null ? "" : " | " + exception));
        }
    }
}
