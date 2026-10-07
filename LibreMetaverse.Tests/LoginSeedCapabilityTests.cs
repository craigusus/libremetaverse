using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;

namespace LibreMetaverse.Tests
{
    /// <summary>
    /// A login reply's seed capability. An empty one used to throw UriFormatException
    /// ("Invalid URI: The URI is empty") out of the reply handler; an invalid one now fails the
    /// login cleanly, before any simulator connection or CAPS setup.
    /// </summary>
    [TestFixture]
    [Category("Login")]
    public class LoginSeedCapabilityTests
    {
        private const string ValidSeed = "https://simhost-0123.agni.secondlife.io:12043/cap/0b6c1a7e-0000-4000-8000-000000000000";

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("\t\n")]
        [TestCase("not a uri")]
        [TestCase("/cap/0b6c1a7e-0000-4000-8000-000000000000")]
        [TestCase("https://")]
        [TestCase("slcaps://seed/cap/0b6c1a7e")]
        [TestCase("file:///etc/passwd")]
        public void ParseRejectsMissingOrInvalidSeed(string seed)
        {
            Assert.That(NetworkManager.ParseSeedCapability(seed), Is.Null);
        }

        [TestCase(ValidSeed)]
        [TestCase("http://127.0.0.1:9000/CAPS/0b6c1a7e-0000-4000-8000-000000000000/")]
        public void ParseAcceptsHttpAndHttpsUnchanged(string seed)
        {
            Assert.That(NetworkManager.ParseSeedCapability(seed), Is.EqualTo(new Uri(seed)));
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("not a uri")]
        public async Task InvalidSeedFailsLoginCleanly(string seed)
        {
            var client = new GridClient();
            var statuses = Track(client);

            await HandleLoginReply(client, LoginReply(seed));

            Assert.That(client.Network.LoginStatusCode, Is.EqualTo(LoginStatus.Failed));
            Assert.That(client.Network.LoginMessage, Is.EqualTo("Login server did not return a valid seed capability"));
            Assert.That(client.Network.LoginErrorKey, Is.EqualTo("bad seed capability"));
            Assert.That(client.Network.LoginSeedCapability, Is.Null);
            Assert.That(statuses, Does.Not.Contain(LoginStatus.ConnectingToSim), "Must not continue towards the simulator");
            Assert.That(client.Network.CurrentSim, Is.Null, "No simulator, so no CAPS");
            Assert.That(client.Network.LoginMessage, Does.Not.Contain("Invalid URI"));
        }

        [Test]
        public async Task ValidSeedIsUnchanged()
        {
            // No sim_ip in the reply, so the login stops before any UDP connection; the seed is
            // still taken exactly as before.
            var client = new GridClient();
            var statuses = Track(client);

            await HandleLoginReply(client, LoginReply(ValidSeed));

            Assert.That(client.Network.LoginSeedCapability, Is.EqualTo(new Uri(ValidSeed)));
            Assert.That(statuses, Does.Contain(LoginStatus.ConnectingToSim));
            Assert.That(client.Network.LoginMessage, Is.EqualTo("Login server did not return a simulator address"));
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("not a uri")]
        public void LoginFromResponseDataDoesNotThrowOnInvalidSeed(string seed)
        {
            var client = new GridClient();
            var response = new LoginResponseData { SeedCapability = seed };

            bool result = true;
            Assert.DoesNotThrow(() => result = client.Network.Login(response));
            Assert.That(result, Is.False);
            Assert.That(client.Network.LoginSeedCapability, Is.Null);
        }

        private static List<LoginStatus> Track(GridClient client)
        {
            var statuses = new List<LoginStatus>();
            client.Network.LoginProgress += (_, e) => { lock (statuses) statuses.Add(e.Status); };
            return statuses;
        }

        private static byte[] LoginReply(string seed) => Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\"?><llsd><map>" +
            "<key>login</key><string>true</string>" +
            "<key>message</key><string>Welcome</string>" +
            "<key>agent_id</key><uuid>11111111-2222-4333-8444-555555555555</uuid>" +
            "<key>session_id</key><uuid>22222222-3333-4444-8555-666666666666</uuid>" +
            "<key>circuit_code</key><integer>123456</integer>" +
            "<key>region_x</key><integer>256000</integer>" +
            "<key>region_y</key><integer>256000</integer>" +
            "<key>seed_capability</key><string>" + System.Security.SecurityElement.Escape(seed) + "</string>" +
            "</map></llsd>");

        private static Task HandleLoginReply(GridClient client, byte[] reply)
        {
            var handler = typeof(NetworkManager).GetMethod("LoginReplyLLSDHandler", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(handler, Is.Not.Null);
            return (Task)handler!.Invoke(client.Network, new object[] { new HttpResponseMessage(System.Net.HttpStatusCode.OK), reply, null });
        }
    }
}
