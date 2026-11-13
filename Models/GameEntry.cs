namespace SteamStreamer.Models
{
    public sealed class GameEntry
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public bool IsRunning { get; set; }
    }
}
