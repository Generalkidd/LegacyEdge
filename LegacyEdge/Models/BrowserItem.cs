using System;

namespace LegacyEdge.Models
{
    public sealed class BrowserItem
    {
        public string Title { get; set; }
        public string Url { get; set; }
        public string IconUrl { get; set; }
        public string Token { get; set; }
        public string Folder { get; set; }
        public DateTimeOffset Added { get; set; } = DateTimeOffset.Now;
        public string Detail => Added.ToString("g");
    }
}
