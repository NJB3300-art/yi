using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Security.Cryptography.Certificates;
using Windows.Web.Http;
using Windows.Web.Http.Filters;
using SteamStreamer.Models;

namespace SteamStreamer.Services
{
    /// <summary>
    /// Minimal client for the Moonlight/GameStream-style HTTP API that Sunshine exposes.
    /// Pairing is NOT implemented here; reuse Moonlight's pairing code and hand the resulting
    /// client certificate to ConfigureClientCertificate.
    /// </summary>
    public sealed class SunshineClient
    {
        private const int HttpPort = 47989;
        private const int HttpsPort = 47984;

        private readonly string _host;
        private readonly string _uniqueId;
        private HttpClient _https;

        public SunshineClient(string host, string uniqueId)
        {
            _host = host;
            _uniqueId = uniqueId;
        }

        public void ConfigureClientCertificate(Certificate clientCert)
        {
            var filter = new HttpBaseProtocolFilter();
            filter.ClientCertificate = clientCert;
            // Sunshine uses a self-signed server cert; trust is established during pairing.
            filter.IgnorableServerCertificateErrors.Add(ChainValidationResult.Untrusted);
            filter.IgnorableServerCertificateErrors.Add(ChainValidationResult.InvalidName);
            _https = new HttpClient(filter);
        }

        public async Task<bool> IsPairedAsync()
        {
            using (var http = new HttpClient())
            {
                var xml = await GetXmlAsync(http, $"http://{_host}:{HttpPort}/serverinfo?uniqueid={_uniqueId}");
                return (string)xml.Root.Element("PairStatus") == "1";
            }
        }

        public async Task<IReadOnlyList<GameEntry>> GetAppsAsync()
        {
            EnsureHttps();
            var xml = await GetXmlAsync(_https, $"https://{_host}:{HttpsPort}/applist?uniqueid={_uniqueId}");
            var list = new List<GameEntry>();
            foreach (var app in xml.Root.Elements("App"))
            {
                list.Add(new GameEntry
                {
                    Id = int.Parse((string)app.Element("ID")),
                    Title = (string)app.Element("AppTitle"),
                    IsRunning = (string)app.Element("IsRunning") == "1"
                });
            }
            return list;
        }

        public async Task QuitAsync()
        {
            EnsureHttps();
            await GetXmlAsync(_https, $"https://{_host}:{HttpsPort}/cancel?uniqueid={_uniqueId}");
        }

        // The launch request carries the stream session keys (rikey, rikeyid) and display mode.
        // Moonlight's StreamSession code generates these; call it from MainPage.LaunchAsync.
        public Task<Uri> BuildLaunchUriAsync(int appId, string rikeyHex, int rikeyId, int w, int h, int fps)
        {
            var uri = new Uri(
                $"https://{_host}:{HttpsPort}/launch?uniqueid={_uniqueId}&appid={appId}" +
                $"&mode={w}x{h}x{fps}&additionalStates=1&sops=1&rikey={rikeyHex}&rikeyid={rikeyId}");
            return Task.FromResult(uri);
        }

        private void EnsureHttps()
        {
            if (_https == null)
                throw new InvalidOperationException("Call ConfigureClientCertificate after pairing first.");
        }

        private static async Task<XDocument> GetXmlAsync(HttpClient http, string url)
        {
            var text = await http.GetStringAsync(new Uri(url));
            return XDocument.Parse(text);
        }
    }
}
