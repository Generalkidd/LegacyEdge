using LegacyEdge.Services;
using System;
using System.IO;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LegacyEdge
{
    sealed partial class App : Application
    {
        public App()
        {
            UnhandledException += App_UnhandledException;
            try
            {
                InitializeComponent();
                Suspending += OnSuspending;
                Resuming += OnResuming;
            }
            catch (Exception exception)
            {
                WriteStartupFailure("App constructor", exception);
                throw;
            }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            try
            {
                ShowWindow(null);
                ClearStartupFailure();
            }
            catch (Exception exception)
            {
                WriteStartupFailure("OnLaunched", exception);
                throw;
            }
        }

        protected override void OnActivated(IActivatedEventArgs args)
        {
            string address = null;
            if (args.Kind == ActivationKind.Protocol)
            {
                var protocol = (ProtocolActivatedEventArgs)args;
                var raw = protocol.Uri.OriginalString;
                if (raw.StartsWith("legacyedge:", System.StringComparison.OrdinalIgnoreCase))
                    address = System.Uri.UnescapeDataString(raw.Substring("legacyedge:".Length).TrimStart('/'));
            }
            ShowWindow(address);
            ClearStartupFailure();
        }

        protected override void OnFileActivated(FileActivatedEventArgs args)
        {
            try
            {
                var file = args.Files.Count > 0 ? args.Files[0] as StorageFile : null;
                ShowWindow(file);
                ClearStartupFailure();
            }
            catch (Exception exception)
            {
                WriteStartupFailure("OnFileActivated", exception);
                throw;
            }
        }

        private static void App_UnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            WriteStartupFailure("UnhandledException", e.Exception);
        }

        private static void WriteStartupFailure(string stage, Exception exception)
        {
            try
            {
                var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, "startup-error.txt");
                File.WriteAllText(path, DateTimeOffset.Now + Environment.NewLine + stage + Environment.NewLine + exception);
            }
            catch
            {
                // Preserve the original exception if diagnostics cannot be written.
            }
        }

        private static void ClearStartupFailure()
        {
            try
            {
                var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, "startup-error.txt");
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        private static void ShowWindow(object activation)
        {
            var rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null)
            {
                rootFrame = new Frame();
                Window.Current.Content = rootFrame;
            }

            if (rootFrame.Content == null)
                rootFrame.Navigate(typeof(MainPage), activation);
            else if (rootFrame.Content is MainPage page)
            {
                if (activation is string address && !string.IsNullOrWhiteSpace(address)) page.OpenExternalAddress(address);
                else if (activation is StorageFile file) page.OpenExternalFile(file);
            }

            Window.Current.Activate();
        }

        private async void OnSuspending(object sender, Windows.ApplicationModel.SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                if (Window.Current.Content is Frame frame && frame.Content is MainPage page)
                    page.SaveSession();
                await DownloadCoordinator.Current.FlushAsync();
            }
            finally
            {
                deferral.Complete();
            }
        }

        private void OnResuming(object sender, object e) => _ = DownloadCoordinator.Current.RefreshAsync();
    }
}
