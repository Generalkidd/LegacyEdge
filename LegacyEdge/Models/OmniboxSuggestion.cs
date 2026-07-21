using System;

namespace LegacyEdge.Models
{
    public sealed class OmniboxSuggestion
    {
        public string Title { get; set; }
        public string Detail { get; set; }
        public string Address { get; set; }
        public string Glyph { get; set; }
        public string Kind { get; set; }
        public string ActionLabel { get; set; }
        public Guid TabId { get; set; }
        public int Rank { get; set; }
        public bool CanDelete { get; set; }
        public string AutomationName => string.IsNullOrWhiteSpace(Detail) ? Title : Title + ", " + Detail;
    }
}
