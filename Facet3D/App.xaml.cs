using System;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Facet3D
{
    sealed partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            // Xbox: drive the UI with the controller's focus engine instead of the mouse-style pointer.
            RequiresPointerMode = ApplicationRequiresPointerMode.WhenRequested;
            UnhandledException += OnUnhandledException;
        }

        void OnUnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            var page = MainPage.Current;
            if (page != null) page.ShowToast("Error: " + e.Message);
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            // Use the full TV screen; the UI keeps its own title-safe margins.
            ApplicationView.GetForCurrentView().SetDesiredBoundsMode(ApplicationViewBoundsMode.UseCoreWindow);

            var frame = Window.Current.Content as Frame;
            if (frame == null)
            {
                frame = new Frame();
                Window.Current.Content = frame;
            }
            if (frame.Content == null) frame.Navigate(typeof(MainPage), e.Arguments);
            Window.Current.Activate();
        }
    }
}
