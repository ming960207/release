using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;

namespace Filee.App.Views;

/// <summary>A short-lived private browser window opened by Alt + right-click.</summary>
public partial class MiniBrowserWindow : Window
{
    public MiniBrowserWindow()
    {
        InitializeComponent();
        Browser.EnvironmentRequested += OnEnvironmentRequested;
        Browser.NavigationStarted += OnNavigationStarted;
        Browser.NavigationCompleted += OnNavigationCompleted;
        Browser.NewWindowRequested += OnNewWindowRequested;
        BackButton.Click += (_, _) => Browser.GoBack();
        ForwardButton.Click += (_, _) => Browser.GoForward();
        RefreshButton.Click += (_, _) => Browser.Refresh();
        GoButton.Click += (_, _) => Navigate();
        AddressBox.KeyDown += OnAddressKeyDown;
    }

    public void FocusAddress()
    {
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private static void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs args)
    {
        if (args is WindowsWebView2EnvironmentRequestedEventArgs windows)
            windows.IsInPrivateModeEnabled = true;
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs args)
    {
        if (args.Request is { } request)
            AddressBox.Text = request.ToString();
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs args)
    {
        BackButton.IsEnabled = Browser.CanGoBack;
        ForwardButton.IsEnabled = Browser.CanGoForward;
    }

    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (args.Request is { } request && IsWebAddress(request))
            Browser.Navigate(request);
    }

    private void OnAddressKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter)
            return;
        args.Handled = true;
        Navigate();
    }

    private void Navigate()
    {
        var input = AddressBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(input))
            return;

        var uri = Uri.TryCreate(input, UriKind.Absolute, out var absolute) && absolute is not null && IsWebAddress(absolute)
            ? absolute
            : !input.Any(char.IsWhiteSpace) && Uri.TryCreate($"https://{input}", UriKind.Absolute, out var host) && host is not null
                ? host
                : new Uri($"https://www.bing.com/search?q={Uri.EscapeDataString(input)}");
        Browser.Navigate(uri);
    }

    private static bool IsWebAddress(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.AbsoluteUri == "about:blank";

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Browser.Stop();
        base.OnClosing(e);
    }
}
