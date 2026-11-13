using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using SteamStreamer.Models;
using SteamStreamer.Services;

namespace SteamStreamer
{
    public sealed partial class MainPage : Page
    {
        private SunshineClient _client;

        public MainPage()
        {
            InitializeComponent();
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            var host = HostBox.Text.Trim();
            if (host.Length == 0) { StatusText.Text = "Enter your PC's IP address."; return; }

            // Stable per-install id, as Moonlight does.
            var id = Windows.Storage.ApplicationData.Current.LocalSettings.Values["uniqueId"] as string;
            if (id == null)
            {
                id = Guid.NewGuid().ToString("N").Substring(0, 16);
                Windows.Storage.ApplicationData.Current.LocalSettings.Values["uniqueId"] = id;
            }

            _client = new SunshineClient(host, id);
            try
            {
                if (!await _client.IsPairedAsync())
                {
                    // TODO: call Moonlight's pairing flow here, then:
                    // _client.ConfigureClientCertificate(pairedCert);
                    StatusText.Text = "Not paired yet. Run pairing and enter the PIN in Sunshine's web UI.";
                    return;
                }
                GameGrid.ItemsSource = await _client.GetAppsAsync();
                StatusText.Text = "Connected.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Could not reach host: " + ex.Message;
            }
        }

        private async void GameGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            await LaunchAsync((GameEntry)e.ClickedItem);
        }

        private System.Threading.Tasks.Task LaunchAsync(GameEntry game)
        {
            StatusText.Text = "Launching " + game.Title + "...";
            // TODO: hand off to the Moonlight fork's stream session:
            //   1. generate rikey/rikeyid
            //   2. _client.BuildLaunchUriAsync(game.Id, rikeyHex, rikeyId, 1920, 1080, 60)
            //   3. start the video/audio/input session via moonlight-common-c
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }
}
