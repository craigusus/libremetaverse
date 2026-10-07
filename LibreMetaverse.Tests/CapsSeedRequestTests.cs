using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace LibreMetaverse.Tests
{
    /// <summary>
    /// The seed capability request: a bounded, iterative retry. Each failure was once retried by
    /// recursing synchronously through a disposed CancellationTokenSource until the stack overflowed;
    /// these tests drive every failure kind through a scripted handler, without a grid.
    /// </summary>
    [TestFixture]
    [Category("Caps")]
    public class CapsSeedRequestTests
    {
        private static readonly Uri SeedUri = new Uri("https://seed.test/cap/0b6c1a7e-0000-4000-8000-000000000000");
        private static readonly TimeSpan[] NoWait = { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero };
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

        private const string SuccessBody =
            "<?xml version=\"1.0\"?><llsd><map>" +
            "<key>GetDisplayNames</key><string>https://caps.test/cap/display-names</string>" +
            "<key>FetchInventory2</key><string>https://caps.test/cap/fetch-inventory</string>" +
            "</map></llsd>";

        private readonly List<GridClient> _clients = new List<GridClient>();

        [TearDown]
        public void TearDown()
        {
            foreach (var client in _clients)
            {
                try { client.HttpCapsClient?.Dispose(); } catch { }
            }
            _clients.Clear();
        }

        [Test]
        public async Task SuccessAppliesCapabilitiesAndRaisesEventOnce()
        {
            var seed = Start(Respond(HttpStatusCode.OK, SuccessBody));
            await seed.Finish();

            Assert.That(seed.Caps.SeedState, Is.EqualTo(Caps.SeedRequestState.Succeeded));
            Assert.That(seed.Handler.Calls, Is.EqualTo(1));
            Assert.That(seed.Received, Is.EqualTo(1));
            Assert.That(seed.Failures, Is.Empty);
            Assert.That(seed.Caps.CapabilityURI("GetDisplayNames"), Is.EqualTo(new Uri("https://caps.test/cap/display-names")));
            Assert.That(seed.Caps.CapabilityURI("FetchInventory2"), Is.EqualTo(new Uri("https://caps.test/cap/fetch-inventory")));
            Assert.That(seed.Handler.Methods, Is.All.EqualTo(HttpMethod.Post));
            Assert.That(seed.Handler.Uris, Is.All.EqualTo(SeedUri));
        }

        [Test]
        public async Task TransportExceptionIsRetried()
        {
            var seed = Start(Throw(), Respond(HttpStatusCode.OK, SuccessBody));
            await seed.Finish();

            Assert.That(seed.Caps.SeedState, Is.EqualTo(Caps.SeedRequestState.Succeeded));
            Assert.That(seed.Handler.Calls, Is.EqualTo(2));
            Assert.That(seed.Received, Is.EqualTo(1));
            Assert.That(seed.Failures, Is.Empty);
        }

        [Test]
        public async Task TransientFailuresThenSuccessRaiseEventOnce()
        {
            var seed = Start(Throw(), Respond(HttpStatusCode.ServiceUnavailable, "busy"), Respond(HttpStatusCode.OK, ""),
                Respond(HttpStatusCode.OK, SuccessBody));
            await seed.Finish();

            Assert.That(seed.Caps.SeedState, Is.EqualTo(Caps.SeedRequestState.Succeeded));
            Assert.That(seed.Handler.Calls, Is.EqualTo(4));
            Assert.That(seed.Received, Is.EqualTo(1));
            Assert.That(seed.Failures, Is.Empty);
        }

        [Test]
        public async Task RepeatedTransportFailureStopsAtRetryLimit()
        {
            var seed = Start(Throw());
            await seed.Finish();
            AssertFailedAfter(seed, 5);
        }

        [Test]
        public async Task PermanentServerErrorStopsAtRetryLimit()
        {
            var seed = Start(Respond(HttpStatusCode.InternalServerError, "<html><body>500</body></html>", "text/html"));
            await seed.Finish();
            AssertFailedAfter(seed, 5);
        }

        [Test]
        public async Task ServerErrorsAreRetried()
        {
            var seed = Start(Respond(HttpStatusCode.ServiceUnavailable, ""), Respond(HttpStatusCode.BadGateway, "<html>502</html>", "text/html"),
                Respond(HttpStatusCode.OK, SuccessBody));
            await seed.Finish();

            Assert.That(seed.Caps.SeedState, Is.EqualTo(Caps.SeedRequestState.Succeeded));
            Assert.That(seed.Handler.Calls, Is.EqualTo(3));
            Assert.That(seed.Received, Is.EqualTo(1));
            Assert.That(seed.Failures, Is.Empty);
        }

        [Test]
        public async Task NotFoundIsTerminal()
        {
            var seed = Start(Respond(HttpStatusCode.NotFound, "cap not found: '0b6c1a7e-0000-4000-8000-000000000000'", "text/plain"),
                Respond(HttpStatusCode.OK, SuccessBody));
            await seed.Finish();
            AssertFailedAfter(seed, 1);
        }

        [Test]
        public async Task EmptyBodyIsAFailedAttempt()
        {
            var seed = Start(Respond(HttpStatusCode.OK, ""));
            await seed.Finish();
            AssertFailedAfter(seed, 5);
        }

        [TestCase("cap not found", "text/plain")]
        [TestCase("<html><body>Bad gateway</body></html>", "text/html")]
        [TestCase("<?xml version=\"1.0\"?><llsd><map><key>Broken", "application/llsd+xml")]
        [TestCase("<?xml version=\"1.0\"?><llsd><array><string>x</string></array></llsd>", "application/llsd+xml")]
        public async Task MalformedOrNonLlsdBodyIsAFailedAttempt(string body, string mediaType)
        {
            var seed = Start(Respond(HttpStatusCode.OK, body, mediaType));
            await seed.Finish();
            AssertFailedAfter(seed, 5);
        }

        [Test]
        public async Task TimeoutIsRetriedThenStops()
        {
            var seed = Start(NoWait, client => client.HttpCapsClient.Timeout = TimeSpan.FromMilliseconds(100), Hang());
            await seed.Finish();
            AssertFailedAfter(seed, 5);
        }

        [Test]
        public async Task TimeoutThenSuccess()
        {
            var seed = Start(NoWait, client => client.HttpCapsClient.Timeout = TimeSpan.FromMilliseconds(100), Hang(),
                Respond(HttpStatusCode.OK, SuccessBody));
            await seed.Finish();

            Assert.That(seed.Caps.SeedState, Is.EqualTo(Caps.SeedRequestState.Succeeded));
            Assert.That(seed.Handler.Calls, Is.EqualTo(2));
            Assert.That(seed.Received, Is.EqualTo(1));
            Assert.That(seed.Failures, Is.Empty);
        }

        [Test]
        public async Task CancellationDuringRequestStopsQuietly()
        {
            var seed = Start(Hang());
            await seed.Handler.WaitForCalls(1);

            seed.Caps.Disconnect(true);
            await seed.Finish();

            AssertCancelledAfter(seed, 1);
        }

        [Test]
        public async Task CancellationDuringBackoffStopsWithoutWaiting()
        {
            var longWaits = Enumerable.Repeat(TimeSpan.FromMinutes(10), 4).ToArray();
            var seed = Start(longWaits, null, Throw());
            await seed.Handler.WaitForCalls(1);
            await Task.Delay(200); // the failed attempt is now waiting out its backoff

            var stopwatch = Stopwatch.StartNew();
            seed.Caps.Disconnect(true);
            await seed.Finish();

            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
            AssertCancelledAfter(seed, 1);
        }

        [Test]
        public async Task NoRetryAfterClientDisconnects()
        {
            var waits = Enumerable.Repeat(TimeSpan.FromMilliseconds(300), 4).ToArray();
            var seed = Start(waits, null, Throw());
            await seed.Handler.WaitForCalls(1);

            SetConnected(seed.Client, false);
            await seed.Finish();

            AssertCancelledAfter(seed, 1);
        }

        [Test]
        public async Task NoRequestWhenClientNotConnected()
        {
            var handler = new ScriptedHandler(Respond(HttpStatusCode.OK, SuccessBody));
            handler.Open();
            var client = NewClient(handler, connected: false);
            var caps = new Caps(NewSimulator(client), SeedUri, NoWait);
            await Bounded(caps.SeedRequest);

            Assert.That(caps.SeedState, Is.EqualTo(Caps.SeedRequestState.Cancelled));
            Assert.That(handler.Calls, Is.EqualTo(0));
        }

        [Test]
        public async Task FailingSubscriberIsNotRetried()
        {
            var seed = Start(Respond(HttpStatusCode.OK, SuccessBody));
            seed.Caps.CapabilitiesReceived += (_, __) => throw new InvalidOperationException("subscriber failure");
            await seed.Finish();

            Assert.That(seed.Caps.SeedState, Is.EqualTo(Caps.SeedRequestState.Succeeded));
            Assert.That(seed.Handler.Calls, Is.EqualTo(1));
            Assert.That(seed.Received, Is.EqualTo(1));
            Assert.That(seed.Failures, Is.Empty);
        }

        [Test]
        public async Task StackDepthStaysFlatAcrossRetries()
        {
            // Synchronous failures were the worst case: every retry re-entered on the same stack.
            var seed = Start(Throw());
            await seed.Finish();

            AssertFailedAfter(seed, 5);
            AssertFlat(seed.Handler.Depths);
        }

        [Test]
        public async Task FailuresAtTheOldOverflowScaleTerminateNormally()
        {
            // The recursion overflowed after about 1,150 attempts (914 lines in production).
            // Thousands of synchronous failures now run in one flat loop and end in Failed.
            const int attempts = 3000;
            var seed = Start(Enumerable.Repeat(TimeSpan.Zero, attempts - 1).ToArray(), null, Throw());
            await seed.Finish();

            AssertFailedAfter(seed, attempts);
            AssertFlat(seed.Handler.Depths);
        }

        [Test]
        public async Task OneClientFailingDoesNotAffectAnother()
        {
            var waits = Enumerable.Repeat(TimeSpan.FromMilliseconds(100), 4).ToArray();
            var failing = Start(waits, null, Throw());
            var healthy = Start(Respond(HttpStatusCode.OK, SuccessBody));

            await healthy.Finish();
            Assert.That(healthy.Caps.SeedState, Is.EqualTo(Caps.SeedRequestState.Succeeded));
            Assert.That(failing.Caps.SeedState, Is.EqualTo(Caps.SeedRequestState.Pending),
                "The healthy client finished while the failing one was still in its backoff");

            await failing.Finish();
            AssertFailedAfter(failing, 5);
            Assert.That(healthy.Received, Is.EqualTo(1));
            Assert.That(healthy.Failures, Is.Empty);
            Assert.That(healthy.Caps.Capabilities(), Has.Count.EqualTo(2));
            Assert.That(healthy.Handler.Calls, Is.EqualTo(1));
        }

        [Test]
        public async Task CancellationIsPerClient()
        {
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = Start(Hang());
            var other = Start(async (request, token) =>
            {
                await release.Task.ConfigureAwait(false);
                return Response(HttpStatusCode.OK, SuccessBody);
            });
            await Task.WhenAll(cancelled.Handler.WaitForCalls(1), other.Handler.WaitForCalls(1));

            cancelled.Caps.Disconnect(true);
            await cancelled.Finish();
            Assert.That(other.Caps.SeedState, Is.EqualTo(Caps.SeedRequestState.Pending));

            release.SetResult(true);
            await other.Finish();
            AssertCancelledAfter(cancelled, 1);
            Assert.That(other.Caps.SeedState, Is.EqualTo(Caps.SeedRequestState.Succeeded));
            Assert.That(other.Received, Is.EqualTo(1));
            Assert.That(other.Failures, Is.Empty);
        }

        [Test]
        public async Task ConcurrentClientsEachSucceedOnce()
        {
            var seeds = Enumerable.Range(0, 4)
                .Select(i => Start(Throw(), Respond(HttpStatusCode.OK, SuccessBody)))
                .ToList();
            await Task.WhenAll(seeds.Select(s => s.Finish()));

            foreach (var seed in seeds)
            {
                Assert.That(seed.Caps.SeedState, Is.EqualTo(Caps.SeedRequestState.Succeeded));
                Assert.That(seed.Handler.Calls, Is.EqualTo(2));
                Assert.That(seed.Received, Is.EqualTo(1));
            Assert.That(seed.Failures, Is.Empty);
            }
        }

        [Test]
        public async Task FailureSignalCarriesBoundedMetadataOnly()
        {
            var transport = Start(Throw());
            var serverError = Start(Respond(HttpStatusCode.BadGateway, "<html>502</html>", "text/html"));
            var notFound = Start(Respond(HttpStatusCode.NotFound, "cap not found: '0b6c1a7e'", "text/plain"));
            var malformed = Start(Respond(HttpStatusCode.OK, "not llsd at all", "text/plain"));
            var empty = Start(Respond(HttpStatusCode.OK, ""));
            var timeout = Start(NoWait, client => client.HttpCapsClient.Timeout = TimeSpan.FromMilliseconds(100), Hang());
            await Task.WhenAll(transport.Finish(), serverError.Finish(), notFound.Finish(), malformed.Finish(),
                empty.Finish(), timeout.Finish());

            AssertFailedAfter(transport, 5);
            AssertFailure(transport, CapabilitiesFailureKind.Transport, null, nameof(HttpRequestException));
            AssertFailedAfter(serverError, 5);
            AssertFailure(serverError, CapabilitiesFailureKind.HttpError, 502, null);
            AssertFailedAfter(notFound, 1);
            AssertFailure(notFound, CapabilitiesFailureKind.NotFound, 404, null);
            AssertFailedAfter(malformed, 5);
            AssertFailure(malformed, CapabilitiesFailureKind.InvalidResponse, 200, "OSDException");
            AssertFailedAfter(empty, 5);
            AssertFailure(empty, CapabilitiesFailureKind.InvalidResponse, 200, null);
            AssertFailedAfter(timeout, 5);
            Assert.That(timeout.Failures[0].Kind, Is.EqualTo(CapabilitiesFailureKind.Timeout));
            Assert.That(timeout.Failures[0].HttpStatus, Is.Null);

            // Only names and numbers: nothing that could carry the seed or a capability URL.
            var strings = typeof(CapabilitiesFailedEventArgs).GetProperties()
                .Where(property => property.PropertyType == typeof(string)).Select(property => property.Name);
            Assert.That(strings, Is.EquivalentTo(new[] { nameof(CapabilitiesFailedEventArgs.ExceptionType) }));
        }

        [Test]
        public async Task NoFailureSignalWhenDisconnectedDuringTheLastAttempt()
        {
            // The last attempt fails just as the client goes offline (a deliberate logout):
            // a cancellation, never a terminal failure.
            Seed seed = null;
            seed = Start(Throw(), Throw(), Throw(), Throw(), (request, token) =>
            {
                SetConnected(seed.Client, false);
                throw new HttpRequestException("simulated connection reset");
            });
            await seed.Finish();

            AssertCancelledAfter(seed, 5);
        }

        [Test]
        public async Task NoFailureSignalWhenClientDisconnectsAsA404Arrives()
        {
            // A 404 that arrives as the client goes offline (logout) is not a failure either.
            Seed seed = null;
            seed = Start((request, token) =>
            {
                SetConnected(seed.Client, false);
                return Task.FromResult(Response(HttpStatusCode.NotFound, "cap not found", "text/plain"));
            });
            await seed.Finish();

            AssertCancelledAfter(seed, 1);
        }

        [Test]
        public async Task NoFailureSignalWhenCapsDisconnectedDuringTheRequest()
        {
            Seed seed = null;
            seed = Start((request, token) =>
            {
                seed.Caps.Disconnect(true);
                return Task.FromResult(Response(HttpStatusCode.NotFound, "cap not found", "text/plain"));
            });
            await seed.Finish();

            AssertCancelledAfter(seed, 1);
        }

        [Test]
        public async Task ThrowingFailureSubscriberDoesNotRetryOrRepeat()
        {
            var seed = Start(Respond(HttpStatusCode.NotFound, "cap not found", "text/plain"));
            seed.Client.Network.CapabilitiesFailed += (_, __) => throw new InvalidOperationException("subscriber failure");
            await seed.Finish();

            AssertFailedAfter(seed, 1);
        }

        [Test]
        public async Task FailureSignalGoesOnlyToTheFailingClient()
        {
            var failing = Start(Throw());
            var healthy = Start(Respond(HttpStatusCode.OK, SuccessBody));
            await Task.WhenAll(failing.Finish(), healthy.Finish());

            AssertFailedAfter(failing, 5);
            Assert.That(healthy.Caps.SeedState, Is.EqualTo(Caps.SeedRequestState.Succeeded));
            Assert.That(healthy.Failures, Is.Empty);
            Assert.That(healthy.Received, Is.EqualTo(1));
        }

        private static void AssertFailure(Seed seed, CapabilitiesFailureKind kind, int? status, string exceptionType)
        {
            var failure = seed.Failures.Single();
            Assert.That(failure.Kind, Is.EqualTo(kind));
            Assert.That(failure.HttpStatus, Is.EqualTo(status));
            Assert.That(failure.ExceptionType, Is.EqualTo(exceptionType));
        }

        private static void AssertFailedAfter(Seed seed, int attempts)
        {
            Assert.That(seed.Caps.SeedState, Is.EqualTo(Caps.SeedRequestState.Failed));
            Assert.That(seed.Failures, Has.Count.EqualTo(1), "CapabilitiesFailed must be raised exactly once");
            Assert.That(seed.Failures[0].Simulator, Is.SameAs(seed.Caps.Simulator));
            Assert.That(seed.Failures[0].Attempts, Is.EqualTo(attempts));
            Assert.That(seed.Handler.Calls, Is.EqualTo(attempts));
            Assert.That(seed.Received, Is.EqualTo(0));
            Assert.That(seed.Caps.Capabilities(), Is.Empty);
            Assert.That(seed.Caps.IsEventQueueRunning, Is.False);
        }

        // The first attempt is released from a thread-pool continuation (Open), so its stack
        // starts elsewhere; every later attempt must run at the same depth as the second.
        private static void AssertFlat(List<int> depths)
        {
            var retries = depths.Skip(1).ToList();
            Assert.That(retries.Max() - retries.Min(), Is.LessThanOrEqualTo(2),
                "Stack depth grew across attempts: " + string.Join(",", retries.Take(20)));
        }

        private static void AssertCancelledAfter(Seed seed, int attempts)
        {
            Assert.That(seed.Caps.SeedState, Is.EqualTo(Caps.SeedRequestState.Cancelled));
            Assert.That(seed.Failures, Is.Empty, "A cancellation is not a failure");
            Assert.That(seed.Handler.Calls, Is.EqualTo(attempts));
            Assert.That(seed.Received, Is.EqualTo(0));
            Assert.That(seed.Caps.Capabilities(), Is.Empty);
        }

        // ---- harness ----

        private delegate Task<HttpResponseMessage> Step(HttpRequestMessage request, CancellationToken token);

        private sealed class Seed
        {
            public GridClient Client;
            public ScriptedHandler Handler;
            public Caps Caps;
            private int _received;
            public int Received => Volatile.Read(ref _received);
            public void OnReceived() => Interlocked.Increment(ref _received);
            public readonly List<CapabilitiesFailedEventArgs> Failures = new List<CapabilitiesFailedEventArgs>();
            public void OnFailed(CapabilitiesFailedEventArgs e) { lock (Failures) Failures.Add(e); }
            public Task Finish() => Bounded(Caps.SeedRequest);
        }

        private Seed Start(params Step[] steps) => Start(NoWait, null, steps);

        private Seed Start(TimeSpan[] waits, Action<GridClient> configure, params Step[] steps)
        {
            var handler = new ScriptedHandler(steps);
            var client = NewClient(handler, connected: true);
            configure?.Invoke(client);
            var seed = new Seed { Client = client, Handler = handler };
            client.Network.CapabilitiesFailed += (_, e) => seed.OnFailed(e);
            // The first attempt waits for the subscription below, so no event can be missed.
            seed.Caps = new Caps(NewSimulator(client), SeedUri, waits);
            seed.Caps.CapabilitiesReceived += (_, __) => seed.OnReceived();
            handler.Open();
            return seed;
        }

        private GridClient NewClient(HttpMessageHandler handler, bool connected)
        {
            var client = new GridClient();
            try { client.Network.Shutdown(NetworkManager.DisconnectType.ClientInitiated, "CapsSeedRequestTests"); } catch { }
            try { client.HttpCapsClient?.Dispose(); } catch { }
            client.HttpCapsClient = new HttpCapsClient(handler);
            SetConnected(client, connected);
            _clients.Add(client);
            return client;
        }

        private static Simulator NewSimulator(GridClient client) =>
            new Simulator(client, new IPEndPoint(IPAddress.Loopback, 0), 0);

        private static void SetConnected(GridClient client, bool connected) =>
            typeof(NetworkManager).GetProperty(nameof(NetworkManager.Connected))!
                .GetSetMethod(true)!.Invoke(client.Network, new object[] { connected });

        private static async Task Bounded(Task task)
        {
            var finished = await Task.WhenAny(task, Task.Delay(Bound)).ConfigureAwait(false);
            Assert.That(finished, Is.SameAs(task), "The seed request did not finish in time");
            await task.ConfigureAwait(false); // never faults
        }

        private static HttpResponseMessage Response(HttpStatusCode status, string body, string mediaType = "application/llsd+xml")
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
            return new HttpResponseMessage(status) { Content = content };
        }

        private static Step Respond(HttpStatusCode status, string body, string mediaType = "application/llsd+xml") =>
            (request, token) => Task.FromResult(Response(status, body, mediaType));

        private static Step Throw() =>
            (request, token) => throw new HttpRequestException("simulated connection reset");

        private static Step Hang() =>
            async (request, token) =>
            {
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                throw new InvalidOperationException("unreachable");
            };

        /// <summary>
        /// Plays the steps in order, repeating the last one. Records each call's stack depth.
        /// The first call waits for Open().
        /// </summary>
        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly Step[] _steps;
            private readonly TaskCompletionSource<bool> _open = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly object _gate = new object();
            private int _calls;

            public ScriptedHandler(params Step[] steps)
            {
                _steps = steps;
            }

            public int Calls => Volatile.Read(ref _calls);
            public List<int> Depths { get; } = new List<int>();
            public List<HttpMethod> Methods { get; } = new List<HttpMethod>();
            public List<Uri> Uris { get; } = new List<Uri>();

            public void Open() => _open.TrySetResult(true);

            public async Task WaitForCalls(int calls)
            {
                var deadline = Stopwatch.StartNew();
                while (Calls < calls)
                {
                    Assert.That(deadline.Elapsed, Is.LessThan(Bound), "The handler was not called in time");
                    await Task.Delay(10).ConfigureAwait(false);
                }
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                if (!_open.Task.IsCompleted)
                {
                    return WaitThenSend(request, token);
                }
                return Send(request, token);
            }

            private async Task<HttpResponseMessage> WaitThenSend(HttpRequestMessage request, CancellationToken token)
            {
                await _open.Task.ConfigureAwait(false);
                return await Send(request, token).ConfigureAwait(false);
            }

            private Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken token)
            {
                int call;
                lock (_gate)
                {
                    Depths.Add(new StackTrace().FrameCount);
                    Methods.Add(request.Method);
                    Uris.Add(request.RequestUri);
                    call = _calls;
                    Interlocked.Increment(ref _calls);
                }
                return _steps[Math.Min(call, _steps.Length - 1)](request, token);
            }
        }
    }
}
