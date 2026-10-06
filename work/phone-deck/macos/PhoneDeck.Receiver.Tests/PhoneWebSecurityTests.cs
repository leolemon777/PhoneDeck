using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhoneDeck.Desktop;

namespace PhoneDeck.PhoneWeb.Tests;

[TestClass]
public sealed class PhoneWebSecurityTests
{
    [TestMethod]
    public void BrowserAdministrationRequiresLoopbackHostPortAndOriginEvenForGet()
    {
        foreach (var method in new[] { "GET", "POST" })
        {
            DefaultHttpContext Context()
            {
                var c = new DefaultHttpContext();
                c.Connection.LocalPort = 8765; c.Connection.RemoteIpAddress = IPAddress.Loopback;
                c.Request.Host = new HostString("127.0.0.1", 8765); c.Request.Method = method;
                return c;
            }
            Assert.IsTrue(LegacyPhoneWebHost.LocalAllowed(Context()));
            var c = Context(); c.Connection.LocalPort = 8768;
            Assert.IsFalse(LegacyPhoneWebHost.LocalAllowed(c));
            c = Context(); c.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.10");
            Assert.IsFalse(LegacyPhoneWebHost.LocalAllowed(c));
            c = Context(); c.Request.Host = new HostString("evil.test", 8765);
            Assert.IsFalse(LegacyPhoneWebHost.LocalAllowed(c));
            c = Context(); c.Request.Headers.Origin = "http://evil.test";
            Assert.IsFalse(LegacyPhoneWebHost.LocalAllowed(c));
            c = Context(); c.Request.Headers.Referer = "http://127.0.0.1:8765.evil.test/";
            Assert.IsFalse(LegacyPhoneWebHost.LocalAllowed(c));
            c = Context(); c.Request.Headers["Sec-Fetch-Site"] = "cross-site";
            Assert.IsFalse(LegacyPhoneWebHost.LocalAllowed(c));
            c = Context(); c.Request.Headers.Origin = "http://127.0.0.1:8765";
            Assert.IsTrue(LegacyPhoneWebHost.LocalAllowed(c));
        }
    }
}
