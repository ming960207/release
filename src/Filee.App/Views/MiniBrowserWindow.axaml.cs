using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;

namespace Filee.App.Views;

/// <summary>A temporary browser window with a persistent profile, opened by Alt + right-click.</summary>
public partial class MiniBrowserWindow : Window
{
    private readonly string _userDataFolder;

    public MiniBrowserWindow(string homeAddress, string userDataFolder)
    {
        _userDataFolder = userDataFolder;
        Directory.CreateDirectory(userDataFolder);
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
        AddressBox.Text = homeAddress;
        Browser.Source = ResolveAddress(homeAddress);
    }

    public void FocusAddress()
    {
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs args)
    {
        if (args is WindowsWebView2EnvironmentRequestedEventArgs windows)
        {
            windows.UserDataFolder = _userDataFolder;
            windows.IsInPrivateModeEnabled = false;
        }
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
        if (string.IsNullOrWhiteSpace(AddressBox.Text))
            return;
        var uri = ResolveAddress(AddressBox.Text);
        Browser.Navigate(uri);
    }

    private static Uri ResolveAddress(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
            return new Uri("about:blank");
        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) && absolute is not null && IsWebAddress(absolute))
            return absolute;
        if (!text.Any(char.IsWhiteSpace) && Uri.TryCreate($"https://{text}", UriKind.Absolute, out var host) && host is not null)
            return host;
        return new Uri($"https://www.bing.com/search?q={Uri.EscapeDataString(text)}");
    }

    private static bool IsWebAddress(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.AbsoluteUri == "about:blank";

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Browser.Stop();
        base.OnClosing(e);
    }
}
