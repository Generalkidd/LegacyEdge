using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Windows.UI.Xaml;

namespace LegacyEdge.Models
{
    public enum LegacyDownloadState
    {
        Queued,
        Running,
        Paused,
        WaitingForNetwork,
        WaitingForUnmeteredNetwork,
        PausedBySystem,
        WaitingToRetry,
        Completed,
        Failed,
        Canceled,
        Missing
    }

    // This is both the persisted download DTO and the per-view presentation model.
    // DownloadCoordinator never publishes its live instances; callers receive detached copies.
    public sealed class DownloadItem : INotifyPropertyChanged
    {
        private string _title;
        private string _sourceUrl;
        private string _fileToken;
        private string _filePath;
        private string _operationId;
        private LegacyDownloadState _state;
        private ulong _bytesReceived;
        private ulong _totalBytes;
        private string _errorMessage;
        private DateTimeOffset? _completedAt;

        public string Id { get; set; } = Guid.NewGuid().ToString("D");

        public string Title
        {
            get => _title;
            set => SetField(ref _title, value);
        }

        public string SourceUrl
        {
            get => _sourceUrl;
            set
            {
                if (SetField(ref _sourceUrl, value))
                    OnPropertyChanged(nameof(SourceText));
            }
        }

        public string FileToken
        {
            get => _fileToken;
            set => SetField(ref _fileToken, value);
        }

        public string FilePath
        {
            get => _filePath;
            set => SetField(ref _filePath, value);
        }

        public string OperationId
        {
            get => _operationId;
            set => SetField(ref _operationId, value);
        }

        public DateTimeOffset Added { get; set; } = DateTimeOffset.Now;

        public DateTimeOffset? CompletedAt
        {
            get => _completedAt;
            set
            {
                if (SetField(ref _completedAt, value))
                {
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(HubStatusText));
                }
            }
        }

        public LegacyDownloadState State
        {
            get => _state;
            set
            {
                if (!SetField(ref _state, value)) return;
                NotifyPresentationChanged();
            }
        }

        public ulong BytesReceived
        {
            get => _bytesReceived;
            set
            {
                if (!SetField(ref _bytesReceived, value)) return;
                OnPropertyChanged(nameof(ProgressValue));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(HubStatusText));
            }
        }

        public ulong TotalBytes
        {
            get => _totalBytes;
            set
            {
                if (!SetField(ref _totalBytes, value)) return;
                OnPropertyChanged(nameof(ProgressValue));
                OnPropertyChanged(nameof(IsIndeterminate));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(HubStatusText));
            }
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            set
            {
                if (SetField(ref _errorMessage, value))
                {
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(HubStatusText));
                }
            }
        }

        public string SourceText
        {
            get
            {
                if (Uri.TryCreate(SourceUrl, UriKind.Absolute, out Uri uri) && !string.IsNullOrWhiteSpace(uri.Host))
                    return uri.Host;
                return SourceUrl ?? string.Empty;
            }
        }

        public string StatusText
        {
            get
            {
                switch (State)
                {
                    case LegacyDownloadState.Queued:
                        return "Starting\u2026";
                    case LegacyDownloadState.Running:
                        return ProgressText("Downloading");
                    case LegacyDownloadState.Paused:
                        return ProgressText("Paused");
                    case LegacyDownloadState.WaitingForNetwork:
                        return ProgressText("Waiting for a network connection");
                    case LegacyDownloadState.WaitingForUnmeteredNetwork:
                        return ProgressText("Waiting for an unmetered network");
                    case LegacyDownloadState.PausedBySystem:
                        return ProgressText("Paused by Windows");
                    case LegacyDownloadState.WaitingToRetry:
                        return string.IsNullOrWhiteSpace(ErrorMessage)
                            ? ProgressText("Waiting to retry")
                            : "Waiting to retry \u2014 " + ErrorMessage;
                    case LegacyDownloadState.Completed:
                        var size = Math.Max(BytesReceived, TotalBytes);
                        var completed = CompletedAt ?? Added;
                        return (size > 0 ? FormatBytes(size) + "  \u2022  " : string.Empty) + "Downloaded " + completed.ToString("g");
                    case LegacyDownloadState.Canceled:
                        return "Canceled";
                    case LegacyDownloadState.Missing:
                        return "File moved or deleted";
                    default:
                        return string.IsNullOrWhiteSpace(ErrorMessage)
                            ? "Couldn't download"
                            : "Couldn't download \u2014 " + ErrorMessage;
                }
            }
        }

        public string HubStatusText
        {
            get
            {
                var amount = BytesReceived > 0 ? FormatBytes(BytesReceived) + "  " : string.Empty;
                var percent = TotalBytes > 0 ? ((int)Math.Round(ProgressValue)) + "%" : string.Empty;
                switch (State)
                {
                    case LegacyDownloadState.Running:
                        return amount + "Downloading" + (percent.Length > 0 ? " - " + percent : "\u2026");
                    case LegacyDownloadState.Paused:
                        return amount + "Paused" + (percent.Length > 0 ? " - " + percent : string.Empty);
                    case LegacyDownloadState.WaitingForNetwork:
                        return amount + "Waiting for a network connection";
                    case LegacyDownloadState.WaitingForUnmeteredNetwork:
                        return amount + "Waiting for an unmetered network";
                    case LegacyDownloadState.PausedBySystem:
                        return amount + "Paused by Windows";
                    case LegacyDownloadState.WaitingToRetry:
                        return amount + "Waiting to retry";
                    default:
                        return StatusText;
                }
            }
        }

        public double ProgressValue => TotalBytes == 0 ? 0 : Math.Min(100, BytesReceived * 100d / TotalBytes);
        public bool IsIndeterminate => TotalBytes == 0 && (State == LegacyDownloadState.Queued || State == LegacyDownloadState.Running);
        public bool IsActive => IsActiveState(State);
        public bool IsTerminal => !IsActive;
        public bool IsFailure => State == LegacyDownloadState.Failed || State == LegacyDownloadState.Canceled || State == LegacyDownloadState.Missing;
        public bool CanPause => State == LegacyDownloadState.Running;
        public bool CanResume => State == LegacyDownloadState.Paused;
        public bool CanCancel => IsActive;
        public bool CanRetry => State == LegacyDownloadState.Failed || State == LegacyDownloadState.Canceled || State == LegacyDownloadState.Missing;
        public bool CanOpen => State == LegacyDownloadState.Completed;

        public Visibility ActiveVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
        public Visibility TerminalVisibility => IsTerminal ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FailureVisibility => IsFailure ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ProgressVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PauseVisibility => CanPause ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ResumeVisibility => CanResume ? Visibility.Visible : Visibility.Collapsed;
        public Visibility CancelVisibility => CanCancel ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RetryVisibility => CanRetry ? Visibility.Visible : Visibility.Collapsed;
        public Visibility OpenVisibility => CanOpen ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RemoveVisibility => IsTerminal ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler PropertyChanged;

        internal static bool IsActiveState(LegacyDownloadState state)
        {
            return state == LegacyDownloadState.Queued ||
                   state == LegacyDownloadState.Running ||
                   state == LegacyDownloadState.Paused ||
                   state == LegacyDownloadState.WaitingForNetwork ||
                   state == LegacyDownloadState.WaitingForUnmeteredNetwork ||
                   state == LegacyDownloadState.PausedBySystem ||
                   state == LegacyDownloadState.WaitingToRetry;
        }

        internal DownloadItem CloneSnapshot()
        {
            return new DownloadItem
            {
                Id = Id,
                Title = Title,
                SourceUrl = SourceUrl,
                FileToken = FileToken,
                FilePath = FilePath,
                OperationId = OperationId,
                Added = Added,
                CompletedAt = CompletedAt,
                State = State,
                BytesReceived = BytesReceived,
                TotalBytes = TotalBytes,
                ErrorMessage = ErrorMessage
            };
        }

        internal void CopyPresentationFrom(DownloadItem source)
        {
            if (source == null) return;
            Title = source.Title;
            SourceUrl = source.SourceUrl;
            FileToken = source.FileToken;
            FilePath = source.FilePath;
            OperationId = source.OperationId;
            Added = source.Added;
            CompletedAt = source.CompletedAt;
            State = source.State;
            BytesReceived = source.BytesReceived;
            TotalBytes = source.TotalBytes;
            ErrorMessage = source.ErrorMessage;
            NotifyPresentationChanged();
        }

        internal void RefreshPresentation() => NotifyPresentationChanged();

        private string ProgressText(string prefix)
        {
            if (TotalBytes > 0)
                return prefix + " \u2014 " + FormatBytes(BytesReceived) + " of " + FormatBytes(TotalBytes) + " (" + ((int)Math.Round(ProgressValue)) + "%)";
            if (BytesReceived > 0) return prefix + " \u2014 " + FormatBytes(BytesReceived);
            return prefix + (State == LegacyDownloadState.Running || State == LegacyDownloadState.Queued ? "\u2026" : string.Empty);
        }

        private static string FormatBytes(ulong bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            var value = (double)bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return (unit == 0 ? value.ToString("0") : value.ToString(value >= 10 ? "0.0" : "0.00")) + " " + units[unit];
        }

        private void NotifyPresentationChanged()
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(HubStatusText));
            OnPropertyChanged(nameof(ProgressValue));
            OnPropertyChanged(nameof(IsIndeterminate));
            OnPropertyChanged(nameof(ProgressVisibility));
            OnPropertyChanged(nameof(PauseVisibility));
            OnPropertyChanged(nameof(ResumeVisibility));
            OnPropertyChanged(nameof(CancelVisibility));
            OnPropertyChanged(nameof(RetryVisibility));
            OnPropertyChanged(nameof(OpenVisibility));
            OnPropertyChanged(nameof(RemoveVisibility));
            OnPropertyChanged(nameof(ActiveVisibility));
            OnPropertyChanged(nameof(TerminalVisibility));
            OnPropertyChanged(nameof(FailureVisibility));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(IsTerminal));
            OnPropertyChanged(nameof(IsFailure));
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(CanResume));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanRetry));
            OnPropertyChanged(nameof(CanOpen));
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
