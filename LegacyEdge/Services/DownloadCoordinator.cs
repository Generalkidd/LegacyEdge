using LegacyEdge.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.BackgroundTransfer;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Web;

namespace LegacyEdge.Services
{
    internal sealed class DownloadSnapshotsChangedEventArgs : EventArgs
    {
        public DownloadSnapshotsChangedEventArgs(IReadOnlyList<DownloadItem> snapshots, string changedId)
        {
            Snapshots = snapshots;
            ChangedId = changedId;
        }

        // The items are detached copies. A view can safely merge them into a
        // dispatcher-owned ObservableCollection without sharing mutable state.
        public IReadOnlyList<DownloadItem> Snapshots { get; }
        public string ChangedId { get; }
    }

    internal sealed class DownloadCoordinator
    {
        private sealed class RuntimeTransfer
        {
            public string ItemId { get; set; }
            public DownloadOperation Operation { get; set; }
            public CancellationTokenSource Cancellation { get; set; }
            public Task ObserverTask { get; set; }
            public bool UserCancelRequested { get; set; }
        }

        // Progress<T> captures the caller's SynchronizationContext. This sink does
        // not, which keeps the broker observer independent of whichever Edge view
        // happened to create or recover the transfer.
        private sealed class InlineProgress<T> : IProgress<T>
        {
            private readonly Action<T> _handler;

            public InlineProgress(Action<T> handler) => _handler = handler;

            public void Report(T value)
            {
                try { _handler?.Invoke(value); }
                catch { }
            }
        }

        private const string ManagedTokenPrefix = "LegacyEdge.Download.";
        private const int MaximumManagedTransfers = 190;
        private static readonly TimeSpan ProgressPersistenceInterval = TimeSpan.FromSeconds(1);
        private readonly object _sync = new object();
        private readonly object _fileAccessSync = new object();
        private readonly object _persistenceSync = new object();
        private readonly SemaphoreSlim _initializeGate = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, DownloadItem> _records = new Dictionary<string, DownloadItem>(StringComparer.Ordinal);
        private readonly Dictionary<Guid, RuntimeTransfer> _runtimeByOperation = new Dictionary<Guid, RuntimeTransfer>();
        private readonly HashSet<Guid> _observedOperationIds = new HashSet<Guid>();
        private readonly HashSet<string> _mutatingItemIds = new HashSet<string>(StringComparer.Ordinal);
        private bool _initialized;
        private int _pendingCreations;
        private string _lastInitializationError;
        private DateTimeOffset _lastProgressPersisted = DateTimeOffset.MinValue;

        private DownloadCoordinator()
        {
            foreach (var loaded in BrowserDataStore.LoadDownloadRecords())
            {
                var item = loaded.CloneSnapshot();
                if (!Guid.TryParse(item.Id, out Guid ignored)) item.Id = Guid.NewGuid().ToString("D");
                _records[item.Id] = item;
            }
        }

        public static DownloadCoordinator Current { get; } = new DownloadCoordinator();

        public event EventHandler<DownloadSnapshotsChangedEventArgs> SnapshotsChanged;

        public bool IsInitialized
        {
            get { lock (_sync) return _initialized; }
        }

        public string LastInitializationError
        {
            get { lock (_sync) return _lastInitializationError; }
        }

        public IReadOnlyList<DownloadItem> GetSnapshots()
        {
            lock (_sync) return CreateSnapshotsLocked();
        }

        public DownloadItem GetSnapshot(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            lock (_sync)
                return _records.TryGetValue(id, out DownloadItem item) ? item.CloneSnapshot() : null;
        }

        public async Task InitializeAsync()
        {
            await _initializeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_sync)
                {
                    if (_initialized) return;
                }

                try
                {
                    await ReconcileCoreAsync().ConfigureAwait(false);
                    lock (_sync)
                    {
                        _initialized = true;
                        _lastInitializationError = null;
                    }
                    CleanupOrphanedManagedTokens();
                    PersistNow();
                    PublishSnapshots(null);
                }
                catch (Exception exception)
                {
                    lock (_sync)
                    {
                        _initialized = false;
                        _lastInitializationError = FriendlyError(exception);
                    }
                    PublishSnapshots(null);
                }
            }
            finally
            {
                _initializeGate.Release();
            }
        }

        public async Task RefreshAsync()
        {
            await InitializeAsync().ConfigureAwait(false);
            if (!IsInitialized) return;

            await _initializeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                try
                {
                    await ReconcileCoreAsync().ConfigureAwait(false);
                    lock (_sync) _lastInitializationError = null;
                    CleanupOrphanedManagedTokens();
                    PersistNow();
                    PublishSnapshots(null);
                }
                catch (Exception exception)
                {
                    lock (_sync) _lastInitializationError = FriendlyError(exception);
                    PublishSnapshots(null);
                }
            }
            finally
            {
                _initializeGate.Release();
            }
        }

        public async Task<StorageFile> CreateDefaultDestinationAsync(string desiredFileName)
        {
            var name = string.IsNullOrWhiteSpace(desiredFileName) ? "download" : desiredFileName;
            return await DownloadsFolder.CreateFileAsync(name, CreationCollisionOption.GenerateUniqueName)
                .AsTask().ConfigureAwait(false);
        }

        public async Task<DownloadItem> EnqueueAsync(Uri source, StorageFile destination)
        {
            ValidateSource(source);
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            await EnsureInitializedAsync().ConfigureAwait(false);

            var item = new DownloadItem
            {
                Id = Guid.NewGuid().ToString("D"),
                Title = destination.Name,
                SourceUrl = source.ToString(),
                FilePath = destination.Path,
                Added = DateTimeOffset.Now,
                State = LegacyDownloadState.Queued
            };

            RuntimeTransfer runtime = null;
            lock (_sync)
            {
                if (_records.Values.Count(record => record.IsActive) + _pendingCreations >= MaximumManagedTransfers)
                    throw new InvalidOperationException("Too many downloads are already active.");
                _pendingCreations++;
            }

            try
            {
                var downloader = new BackgroundDownloader { CostPolicy = BackgroundTransferCostPolicy.Default };
                var operation = downloader.CreateDownload(source, destination);
                lock (_sync)
                {
                    _records[item.Id] = item;
                    item.OperationId = operation.Guid.ToString("D");
                    runtime = CreateRuntimeLocked(item.Id, operation);
                    _pendingCreations--;
                }

                // The operation GUID and destination token are durable before the
                // first StartAsync call, closing the process-termination gap.
                RegisterFileAccess(item.Id, destination);
                PersistNow();
                PublishSnapshots(item.Id);
                StartObserver(runtime, false);
                return GetSnapshot(item.Id);
            }
            catch (Exception exception)
            {
                lock (_sync)
                {
                    if (_pendingCreations > 0 && !_records.ContainsKey(item.Id)) _pendingCreations--;
                    if (!_records.ContainsKey(item.Id)) _records[item.Id] = item;
                }
                RegisterFileAccess(item.Id, destination);
                MarkFailed(item.Id, FriendlyError(exception));
                return GetSnapshot(item.Id);
            }
        }

        public Task<DownloadItem> EnqueueAsync(Uri source, Windows.Storage.IStorageFile destination)
        {
            var storageFile = destination as StorageFile;
            if (storageFile == null) throw new ArgumentException("The destination must be a StorageFile.", nameof(destination));
            return EnqueueAsync(source, storageFile);
        }

        public bool Pause(string id)
        {
            RuntimeTransfer runtime;
            lock (_sync)
            {
                if (!_records.TryGetValue(id ?? string.Empty, out DownloadItem item) || item.State != LegacyDownloadState.Running)
                    return false;
                runtime = FindRuntimeForItemLocked(id);
            }
            if (runtime == null) return false;

            try
            {
                runtime.Operation.Pause();
                ApplyOperationProgress(runtime.ItemId, runtime.Operation, true);
                return true;
            }
            catch
            {
                ApplyOperationProgress(runtime.ItemId, runtime.Operation, true);
                return false;
            }
        }

        public bool Pause(DownloadItem item) => Pause(item?.Id);

        public bool Resume(string id)
        {
            RuntimeTransfer runtime;
            lock (_sync)
            {
                if (!_records.TryGetValue(id ?? string.Empty, out DownloadItem item) || item.State != LegacyDownloadState.Paused)
                    return false;
                runtime = FindRuntimeForItemLocked(id);
            }
            if (runtime == null) return false;

            try
            {
                runtime.Operation.Resume();
                ApplyOperationProgress(runtime.ItemId, runtime.Operation, true);
                return true;
            }
            catch
            {
                ApplyOperationProgress(runtime.ItemId, runtime.Operation, true);
                return false;
            }
        }

        public bool Resume(DownloadItem item) => Resume(item?.Id);

        public bool Cancel(string id)
        {
            RuntimeTransfer runtime;
            lock (_sync)
            {
                if (!_records.TryGetValue(id ?? string.Empty, out DownloadItem item) || !item.IsActive) return false;
                runtime = FindRuntimeForItemLocked(id);
                if (runtime == null) return false;
                runtime.UserCancelRequested = true;
            }

            try
            {
                runtime.Cancellation.Cancel();
                return true;
            }
            catch { return false; }
        }

        public bool Cancel(DownloadItem item) => Cancel(item?.Id);

        public async Task<DownloadItem> RetryAsync(string id, StorageFile replacementFile = null)
        {
            await EnsureInitializedAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(id)) return null;

            Task previousObserver = null;
            DownloadItem snapshot;
            LegacyDownloadState previousState;
            lock (_sync)
            {
                if (_mutatingItemIds.Contains(id) || !_records.TryGetValue(id, out DownloadItem item) || !item.CanRetry)
                    return null;
                _mutatingItemIds.Add(id);
                snapshot = item.CloneSnapshot();
                previousState = item.State;
                var previousRuntime = FindRuntimeForItemLocked(id);
                previousObserver = previousRuntime?.ObserverTask;
                // Claim the record before yielding. Other windows can no longer
                // remove, clear, or independently retry it while this retry is
                // resolving its destination and creating the broker operation.
                item.State = LegacyDownloadState.Queued;
                item.ErrorMessage = null;
            }

            try
            {
                PersistNow();
                PublishSnapshots(id);

                if (previousObserver != null && !previousObserver.IsCompleted)
                {
                    try { await previousObserver.ConfigureAwait(false); }
                    catch { }
                }

                // A completing old observer may have published its terminal state
                // while we awaited it. Reassert the retry claim before any more
                // asynchronous work; the mutation reservation keeps the row alive.
                lock (_sync)
                {
                    if (!_records.TryGetValue(id, out DownloadItem claimed)) return null;
                    claimed.State = LegacyDownloadState.Queued;
                    claimed.ErrorMessage = null;
                }

                var destination = replacementFile ?? await ResolveFileAsync(id).ConfigureAwait(false);
                if (destination == null)
                {
                    RestoreRetryState(id, previousState, true);
                    return null;
                }
                if (!Uri.TryCreate(snapshot.SourceUrl, UriKind.Absolute, out Uri source))
                {
                    MarkFailed(id, "The original download address is no longer valid.");
                    return GetSnapshot(id);
                }
                try { ValidateSource(source); }
                catch
                {
                    MarkFailed(id, "The original download address is no longer valid.");
                    return GetSnapshot(id);
                }
                RegisterFileAccess(id, destination);

                RuntimeTransfer runtime = null;
                try
                {
                    var operation = new BackgroundDownloader { CostPolicy = BackgroundTransferCostPolicy.Default }
                        .CreateDownload(source, destination);
                    lock (_sync)
                    {
                        if (!_mutatingItemIds.Contains(id) || !_records.TryGetValue(id, out DownloadItem current))
                            return null;
                        current.Title = destination.Name;
                        current.FilePath = destination.Path;
                        current.OperationId = operation.Guid.ToString("D");
                        current.Added = DateTimeOffset.Now;
                        current.CompletedAt = null;
                        current.BytesReceived = 0;
                        current.TotalBytes = 0;
                        current.ErrorMessage = null;
                        current.State = LegacyDownloadState.Queued;
                        runtime = CreateRuntimeLocked(current.Id, operation);
                    }

                    PersistNow();
                    PublishSnapshots(id);
                    StartObserver(runtime, false);
                    return GetSnapshot(id);
                }
                catch (Exception exception)
                {
                    MarkFailed(id, FriendlyError(exception));
                    return GetSnapshot(id);
                }
            }
            finally
            {
                lock (_sync) _mutatingItemIds.Remove(id);
            }
        }

        public Task<DownloadItem> RetryAsync(DownloadItem item, StorageFile replacementFile = null)
            => RetryAsync(item?.Id, replacementFile);

        public DownloadItem RecordCompletedFile(StorageFile file, string source)
        {
            if (file == null) return null;
            var now = DateTimeOffset.Now;
            var item = new DownloadItem
            {
                Id = Guid.NewGuid().ToString("D"),
                Title = file.Name,
                SourceUrl = source ?? file.Path,
                FilePath = file.Path,
                Added = now,
                CompletedAt = now,
                State = LegacyDownloadState.Completed
            };

            lock (_sync) _records[item.Id] = item;
            RegisterFileAccess(item.Id, file);
            PersistNow();
            PublishSnapshots(item.Id);
            return GetSnapshot(item.Id);
        }

        public async Task<StorageFile> ResolveFileAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            string token;
            StorageFile runtimeFile = null;
            lock (_sync)
            {
                if (!_records.TryGetValue(id, out DownloadItem item)) return null;
                token = item.FileToken;
                runtimeFile = FindRuntimeForItemLocked(id)?.Operation?.ResultFile as StorageFile;
            }

            if (runtimeFile != null)
            {
                RegisterFileAccess(id, runtimeFile);
                return runtimeFile;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(token) || !StorageApplicationPermissions.FutureAccessList.ContainsItem(token))
                {
                    MarkMissingIfTerminal(id);
                    return null;
                }

                var file = await StorageApplicationPermissions.FutureAccessList.GetFileAsync(token)
                    .AsTask().ConfigureAwait(false);
                lock (_sync)
                {
                    if (_records.TryGetValue(id, out DownloadItem current)) current.FilePath = file.Path;
                }
                return file;
            }
            catch
            {
                MarkMissingIfTerminal(id);
                return null;
            }
        }

        public Task<StorageFile> ResolveFileAsync(DownloadItem item) => ResolveFileAsync(item?.Id);

        public async Task<bool> RemoveAsync(string id, bool deleteFile = false)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            string token;
            lock (_sync)
            {
                if (_mutatingItemIds.Contains(id) || !_records.TryGetValue(id, out DownloadItem item) || item.IsActive)
                    return false;
                _mutatingItemIds.Add(id);
                token = item.FileToken;
            }

            try
            {
                if (deleteFile)
                {
                    var file = await ResolveFileAsync(id).ConfigureAwait(false);
                    if (file != null)
                    {
                        try { await file.DeleteAsync(StorageDeleteOption.Default).AsTask().ConfigureAwait(false); }
                        catch { return false; }
                    }
                }

                lock (_sync)
                {
                    if (!_records.TryGetValue(id, out DownloadItem current) || current.IsActive) return false;
                    _records.Remove(id);
                }
                RemoveFileAccess(token);
                PersistNow();
                PublishSnapshots(id);
                return true;
            }
            finally
            {
                lock (_sync) _mutatingItemIds.Remove(id);
            }
        }

        public Task<bool> RemoveAsync(DownloadItem item, bool deleteFile = false)
            => RemoveAsync(item?.Id, deleteFile);

        public Task<int> ClearTerminalAsync()
        {
            return Task.Run(() =>
            {
                List<string> tokens;
                int count;
                lock (_sync)
                {
                    var terminal = _records.Values
                        .Where(item => item.IsTerminal && !_mutatingItemIds.Contains(item.Id))
                        .ToList();
                    count = terminal.Count;
                    tokens = terminal.Select(item => item.FileToken).Where(token => !string.IsNullOrWhiteSpace(token)).ToList();
                    foreach (var item in terminal) _records.Remove(item.Id);
                }

                foreach (var token in tokens) RemoveFileAccess(token);
                PersistNow();
                PublishSnapshots(null);
                return count;
            });
        }

        public Task FlushAsync() => Task.Run((Action)PersistNow);

        private async Task EnsureInitializedAsync()
        {
            await InitializeAsync().ConfigureAwait(false);
            if (!IsInitialized)
                throw new InvalidOperationException(LastInitializationError ?? "Downloads could not be initialized.");
        }

        private async Task ReconcileCoreAsync()
        {
            var operations = await BackgroundDownloader.GetCurrentDownloadsAsync().AsTask().ConfigureAwait(false);
            var currentIds = new HashSet<Guid>();

            foreach (var operation in operations)
            {
                currentIds.Add(operation.Guid);
                DownloadItem record;
                RuntimeTransfer runtime;
                var resultFile = TryGetResultFile(operation);
                lock (_sync)
                {
                    record = _records.Values.FirstOrDefault(item =>
                        string.Equals(item.OperationId, operation.Guid.ToString("D"), StringComparison.OrdinalIgnoreCase));
                    if (record == null)
                    {
                        record = new DownloadItem
                        {
                            Id = Guid.NewGuid().ToString("D"),
                            Title = resultFile?.Name ?? "download",
                            SourceUrl = operation.RequestedUri?.ToString() ?? string.Empty,
                            OperationId = operation.Guid.ToString("D"),
                            Added = DateTimeOffset.Now,
                            State = LegacyDownloadState.Queued
                        };
                        _records[record.Id] = record;
                    }

                    UpdateFromOperationLocked(record, operation);
                    runtime = CreateRuntimeLocked(record.Id, operation);
                }

                if (resultFile != null) RegisterFileAccess(record.Id, resultFile);
                // Start each observer as soon as it is registered. If a later
                // operation is malformed, earlier GUIDs cannot be stranded in
                // the observed set without a live AttachAsync task.
                StartObserver(runtime, true);
            }

            lock (_sync)
            {
                foreach (var record in _records.Values.Where(item => item.IsActive).ToList())
                {
                    var hasOperation = Guid.TryParse(record.OperationId, out Guid operationId) && currentIds.Contains(operationId);
                    if (!hasOperation && FindRuntimeForItemLocked(record.Id) == null)
                    {
                        record.State = LegacyDownloadState.Failed;
                        record.ErrorMessage = "The transfer is no longer available.";
                    }
                }
            }

            PersistNow();
            PublishSnapshots(null);
        }

        private RuntimeTransfer CreateRuntimeLocked(string itemId, DownloadOperation operation)
        {
            if (operation == null || _observedOperationIds.Contains(operation.Guid)) return null;
            _observedOperationIds.Add(operation.Guid);
            var runtime = new RuntimeTransfer
            {
                ItemId = itemId,
                Operation = operation,
                Cancellation = new CancellationTokenSource()
            };
            _runtimeByOperation[operation.Guid] = runtime;
            return runtime;
        }

        private RuntimeTransfer FindRuntimeForItemLocked(string itemId)
            => _runtimeByOperation.Values.FirstOrDefault(runtime => string.Equals(runtime.ItemId, itemId, StringComparison.Ordinal));

        private void StartObserver(RuntimeTransfer runtime, bool attach)
        {
            if (runtime == null) return;
            var task = ObserveAsync(runtime, attach);
            lock (_sync)
            {
                if (_runtimeByOperation.TryGetValue(runtime.Operation.Guid, out RuntimeTransfer current) && ReferenceEquals(current, runtime))
                    runtime.ObserverTask = task;
            }
        }

        private async Task ObserveAsync(RuntimeTransfer runtime, bool attach)
        {
            try
            {
                var progress = new InlineProgress<DownloadOperation>(operation =>
                    ApplyOperationProgress(runtime.ItemId, operation, false));
                var asyncOperation = attach ? runtime.Operation.AttachAsync() : runtime.Operation.StartAsync();
                var completed = await asyncOperation.AsTask(runtime.Cancellation.Token, progress).ConfigureAwait(false);
                ApplyOperationProgress(runtime.ItemId, completed ?? runtime.Operation, true);

                var status = runtime.Operation.Progress.Status;
                if (status == BackgroundTransferStatus.Completed)
                    Complete(runtime.ItemId, runtime.Operation);
                else if (status == BackgroundTransferStatus.Canceled)
                    MarkCanceled(runtime.ItemId);
                else if (status == BackgroundTransferStatus.Error)
                    MarkFailed(runtime.ItemId, "The download failed.");
            }
            catch (OperationCanceledException)
            {
                MarkCanceled(runtime.ItemId);
            }
            catch (Exception exception)
            {
                if (runtime.UserCancelRequested || SafeStatus(runtime.Operation) == BackgroundTransferStatus.Canceled)
                    MarkCanceled(runtime.ItemId);
                else
                    MarkFailed(runtime.ItemId, FriendlyError(exception));
            }
            finally
            {
                lock (_sync)
                {
                    if (_runtimeByOperation.TryGetValue(runtime.Operation.Guid, out RuntimeTransfer current) && ReferenceEquals(current, runtime))
                        _runtimeByOperation.Remove(runtime.Operation.Guid);
                }
                runtime.Cancellation.Dispose();
                PersistNow();
                PublishSnapshots(runtime.ItemId);
            }
        }

        private void ApplyOperationProgress(string itemId, DownloadOperation operation, bool forcePersist)
        {
            bool stateChanged = false;
            try
            {
                lock (_sync)
                {
                    if (!_records.TryGetValue(itemId, out DownloadItem item)) return;
                    var previous = item.State;
                    UpdateFromOperationLocked(item, operation);
                    stateChanged = previous != item.State;
                }
                PersistIfDue(forcePersist || stateChanged);
                PublishSnapshots(itemId);
            }
            catch { }
        }

        private static void UpdateFromOperationLocked(DownloadItem item, DownloadOperation operation)
        {
            var progress = operation.Progress;
            item.OperationId = operation.Guid.ToString("D");
            item.BytesReceived = progress.BytesReceived;
            item.TotalBytes = progress.TotalBytesToReceive;
            item.State = MapState(progress.Status);

            if (item.State == LegacyDownloadState.WaitingToRetry)
            {
                try
                {
                    WebErrorStatus? status = operation.CurrentWebErrorStatus;
                    item.ErrorMessage = status.HasValue ? FriendlyWebError(status.Value) : null;
                }
                catch { }
            }
            else if (item.State == LegacyDownloadState.Running || item.State == LegacyDownloadState.Queued)
            {
                item.ErrorMessage = null;
            }

            if (item.State == LegacyDownloadState.Completed && !item.CompletedAt.HasValue)
                item.CompletedAt = DateTimeOffset.Now;
        }

        private void Complete(string itemId, DownloadOperation operation)
        {
            var resultFile = operation.ResultFile as StorageFile;
            if (resultFile != null) RegisterFileAccess(itemId, resultFile);
            lock (_sync)
            {
                if (!_records.TryGetValue(itemId, out DownloadItem item)) return;
                item.State = LegacyDownloadState.Completed;
                item.ErrorMessage = null;
                item.CompletedAt = DateTimeOffset.Now;
                item.BytesReceived = operation.Progress.BytesReceived;
                item.TotalBytes = operation.Progress.TotalBytesToReceive;
                if (resultFile != null)
                {
                    item.Title = resultFile.Name;
                    item.FilePath = resultFile.Path;
                }
            }
            PersistNow();
            PublishSnapshots(itemId);
        }

        private void MarkCanceled(string itemId)
        {
            lock (_sync)
            {
                if (!_records.TryGetValue(itemId, out DownloadItem item)) return;
                item.State = LegacyDownloadState.Canceled;
                item.ErrorMessage = null;
            }
            PersistNow();
            PublishSnapshots(itemId);
        }

        private void MarkFailed(string itemId, string message)
        {
            lock (_sync)
            {
                if (!_records.TryGetValue(itemId, out DownloadItem item)) return;
                item.State = LegacyDownloadState.Failed;
                item.ErrorMessage = string.IsNullOrWhiteSpace(message) ? "The download failed." : message;
            }
            PersistNow();
            PublishSnapshots(itemId);
        }

        private void MarkMissingIfTerminal(string itemId)
        {
            lock (_sync)
            {
                if (!_records.TryGetValue(itemId, out DownloadItem item) || item.IsActive) return;
                item.State = LegacyDownloadState.Missing;
                item.ErrorMessage = null;
            }
            PersistNow();
            PublishSnapshots(itemId);
        }

        private void RestoreRetryState(string itemId, LegacyDownloadState previousState, bool destinationMissing)
        {
            lock (_sync)
            {
                if (!_records.TryGetValue(itemId, out DownloadItem item) || item.OperationId != null &&
                    FindRuntimeForItemLocked(itemId) != null) return;
                item.State = destinationMissing ? LegacyDownloadState.Missing : previousState;
                item.ErrorMessage = destinationMissing ? "Choose a new location to retry this download." : null;
            }
            PersistNow();
            PublishSnapshots(itemId);
        }

        private bool RegisterFileAccess(string itemId, StorageFile file)
        {
            if (file == null || string.IsNullOrWhiteSpace(itemId)) return false;
            var token = TokenFor(itemId);
            string previousToken = null;
            try
            {
                lock (_fileAccessSync)
                {
                    StorageApplicationPermissions.FutureAccessList.AddOrReplace(token, file, file.Name);

                    // Keep the FAL mutation and its record reference atomic with
                    // orphan cleanup. Otherwise refresh could snapshot the records
                    // between these two writes and remove this brand-new token.
                    lock (_sync)
                    {
                        if (!_records.TryGetValue(itemId, out DownloadItem item))
                        {
                            if (StorageApplicationPermissions.FutureAccessList.ContainsItem(token))
                                StorageApplicationPermissions.FutureAccessList.Remove(token);
                            return false;
                        }
                        previousToken = item.FileToken;
                        item.FileToken = token;
                        item.FilePath = file.Path;
                        item.Title = file.Name;
                    }

                    if (!string.IsNullOrWhiteSpace(previousToken) &&
                        !string.Equals(previousToken, token, StringComparison.Ordinal) &&
                        StorageApplicationPermissions.FutureAccessList.ContainsItem(previousToken))
                        StorageApplicationPermissions.FutureAccessList.Remove(previousToken);
                }
            }
            catch
            {
                lock (_sync)
                {
                    if (_records.TryGetValue(itemId, out DownloadItem failedItem)) failedItem.FilePath = file.Path;
                }
                return false;
            }
            return true;
        }

        private void RemoveFileAccess(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return;
            try
            {
                lock (_fileAccessSync)
                {
                    if (StorageApplicationPermissions.FutureAccessList.ContainsItem(token))
                        StorageApplicationPermissions.FutureAccessList.Remove(token);
                }
            }
            catch { }
        }

        private void CleanupOrphanedManagedTokens()
        {
            try
            {
                lock (_fileAccessSync)
                {
                    HashSet<string> retained;
                    lock (_sync)
                        retained = new HashSet<string>(_records.Values.Select(item => item.FileToken)
                            .Where(token => !string.IsNullOrWhiteSpace(token)), StringComparer.Ordinal);

                    var removals = new List<string>();
                    foreach (var entry in StorageApplicationPermissions.FutureAccessList.Entries)
                    {
                        if (entry.Token.StartsWith(ManagedTokenPrefix, StringComparison.Ordinal) && !retained.Contains(entry.Token))
                            removals.Add(entry.Token);
                    }
                    foreach (var token in removals) StorageApplicationPermissions.FutureAccessList.Remove(token);
                }
            }
            catch { }
        }

        private static string TokenFor(string itemId)
        {
            if (!Guid.TryParse(itemId, out Guid id)) id = Guid.NewGuid();
            return ManagedTokenPrefix + id.ToString("N");
        }

        private void PersistIfDue(bool force)
        {
            var now = DateTimeOffset.UtcNow;
            lock (_sync)
            {
                if (!force && now - _lastProgressPersisted < ProgressPersistenceInterval) return;
                _lastProgressPersisted = now;
            }
            PersistNow();
        }

        private void PersistNow()
        {
            // Snapshot creation and the store write share this gate so an older
            // progress callback cannot overwrite a newer terminal snapshot.
            lock (_persistenceSync)
            {
                List<DownloadItem> records;
                List<string> prunedTokens;
                lock (_sync)
                {
                    prunedTokens = PruneTerminalRecordsLocked();
                    records = _records.Values.Select(item => item.CloneSnapshot()).ToList();
                    _lastProgressPersisted = DateTimeOffset.UtcNow;
                }

                BrowserDataStore.ReplaceDownloadRecords(records);
                foreach (var token in prunedTokens) RemoveFileAccess(token);
            }
        }

        private List<string> PruneTerminalRecordsLocked()
        {
            var excess = _records.Values
                .Where(item => item.IsTerminal && FindRuntimeForItemLocked(item.Id) == null &&
                    !_mutatingItemIds.Contains(item.Id))
                .OrderByDescending(item => item.CompletedAt ?? item.Added)
                .Skip(100)
                .ToList();
            foreach (var item in excess) _records.Remove(item.Id);
            return excess.Select(item => item.FileToken).Where(token => !string.IsNullOrWhiteSpace(token)).ToList();
        }

        private IReadOnlyList<DownloadItem> CreateSnapshotsLocked()
            => _records.Values.OrderByDescending(item => item.Added).Select(item => item.CloneSnapshot()).ToList().AsReadOnly();

        private void PublishSnapshots(string changedId)
        {
            var handlers = SnapshotsChanged;
            if (handlers == null) return;
            foreach (EventHandler<DownloadSnapshotsChangedEventArgs> handler in handlers.GetInvocationList())
            {
                try
                {
                    IReadOnlyList<DownloadItem> snapshots;
                    lock (_sync) snapshots = CreateSnapshotsLocked();
                    handler(this, new DownloadSnapshotsChangedEventArgs(snapshots, changedId));
                }
                catch { }
            }
        }

        private static LegacyDownloadState MapState(BackgroundTransferStatus status)
        {
            switch (status)
            {
                case BackgroundTransferStatus.Idle: return LegacyDownloadState.Queued;
                case BackgroundTransferStatus.Running: return LegacyDownloadState.Running;
                case BackgroundTransferStatus.PausedByApplication: return LegacyDownloadState.Paused;
                case BackgroundTransferStatus.PausedNoNetwork: return LegacyDownloadState.WaitingForNetwork;
                case BackgroundTransferStatus.PausedCostedNetwork: return LegacyDownloadState.WaitingForUnmeteredNetwork;
                case BackgroundTransferStatus.PausedSystemPolicy: return LegacyDownloadState.PausedBySystem;
                case BackgroundTransferStatus.PausedRecoverableWebErrorStatus: return LegacyDownloadState.WaitingToRetry;
                case BackgroundTransferStatus.Completed: return LegacyDownloadState.Completed;
                case BackgroundTransferStatus.Canceled: return LegacyDownloadState.Canceled;
                default: return LegacyDownloadState.Failed;
            }
        }

        private static BackgroundTransferStatus SafeStatus(DownloadOperation operation)
        {
            try { return operation.Progress.Status; }
            catch { return BackgroundTransferStatus.Error; }
        }

        private static StorageFile TryGetResultFile(DownloadOperation operation)
        {
            try { return operation?.ResultFile as StorageFile; }
            catch { return null; }
        }

        private static void ValidateSource(Uri source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (!string.Equals(source.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(source.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(source.Scheme, Uri.UriSchemeFtp, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only HTTP, HTTPS, and FTP downloads are supported.", nameof(source));
        }

        private static string FriendlyError(Exception exception)
        {
            if (exception == null) return "The download failed.";
            try { return FriendlyWebError(BackgroundTransferError.GetStatus(exception.HResult)); }
            catch { return "The download failed."; }
        }

        private static string FriendlyWebError(WebErrorStatus status)
        {
            switch (status)
            {
                case WebErrorStatus.HostNameNotResolved: return "The server couldn't be found.";
                case WebErrorStatus.ServerUnreachable:
                case WebErrorStatus.CannotConnect: return "Couldn't connect to the server.";
                case WebErrorStatus.Timeout:
                case WebErrorStatus.RequestTimeout:
                case WebErrorStatus.GatewayTimeout: return "The connection timed out.";
                case WebErrorStatus.Disconnected:
                case WebErrorStatus.ConnectionAborted:
                case WebErrorStatus.ConnectionReset: return "The network connection was interrupted.";
                case WebErrorStatus.Unauthorized:
                case WebErrorStatus.ProxyAuthenticationRequired: return "Sign-in is required to download this file.";
                case WebErrorStatus.Forbidden: return "The server denied access to this file.";
                case WebErrorStatus.NotFound: return "The file wasn't found on the server.";
                case WebErrorStatus.ServiceUnavailable:
                case WebErrorStatus.BadGateway: return "The server is temporarily unavailable.";
                case WebErrorStatus.CertificateCommonNameIsIncorrect:
                case WebErrorStatus.CertificateExpired:
                case WebErrorStatus.CertificateContainsErrors:
                case WebErrorStatus.CertificateRevoked:
                case WebErrorStatus.CertificateIsInvalid: return "The server's security certificate isn't valid.";
                case WebErrorStatus.OperationCanceled: return "The download was canceled.";
                default: return "The download failed.";
            }
        }
    }
}
