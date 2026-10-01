using System;
using System.Collections.Generic;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;

namespace LegacyEdge.Models
{
    internal sealed class BrowserTab
    {
        public Guid Id { get; } = Guid.NewGuid();
        public WebView View { get; set; }
        public int ViewGeneration { get; set; }
        public int NavigationGeneration { get; set; }
        public string PendingAddress { get; set; }
        public string Title { get; set; } = "New tab";
        public string Address { get; set; } = string.Empty;
        public string IconUrl { get; set; }
        public string OriginalAddress { get; set; }
        public string PreviousAddress { get; set; }
        public string PreviousTitle { get; set; }
        public StorageFile LocalFile { get; set; }
        public string LocalFileToken { get; set; }
        public bool IsLoading { get; set; }
        public bool LastNavigationSucceeded { get; set; }
        public bool IsReadingView { get; set; }
        public bool IsPrivate { get; set; }
        public bool IsPinned { get; set; }
        public BitmapImage PreviewImage { get; set; }
        public bool IsPdfView { get; set; }
        public string PdfSourceAddress { get; set; }
        public string PdfReturnAddress { get; set; }
        public string PdfReturnTitle { get; set; }
        public StorageFile PdfFile { get; set; }
        public PdfDocument PdfDocument { get; set; }
        public Dictionary<uint, BitmapImage> PdfPageImages { get; } = new Dictionary<uint, BitmapImage>();
        public double PdfRasterScale { get; set; } = 1;
        public double PdfZoom { get; set; } = 1;
        public bool PdfFitWidth { get; set; } = true;
        public int PdfRotation { get; set; }
        public uint PdfCurrentPage { get; set; } = 1;
        public double PdfVerticalOffset { get; set; }
        public int PdfLoadGeneration { get; set; }
        public bool PdfReturnNeedsReload { get; set; }
        public double ZoomPercent { get; set; } = 100;
        public bool HasPageScrollMetrics { get; set; }
        public bool PageScrollBarVisible { get; set; }
        public double PageScrollMaximum { get; set; }
        public double PageScrollViewport { get; set; }
        public double PageScrollOffset { get; set; }
    }
}
