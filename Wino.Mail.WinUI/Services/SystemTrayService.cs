using System;
using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Wino.Core.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.WinUI;

namespace Wino.Mail.WinUI.Services;

public class SystemTrayService : ISystemTrayService
{
    private TaskbarIcon? _taskbarIcon;
    private bool _isDisposed;
    private bool _isMinimizedToTray;

    public bool IsMinimizedToTray => _isMinimizedToTray;

    public event EventHandler? TrayIconDoubleClicked;

    public void Initialize()
    {
        if (_taskbarIcon != null) return;

        try
        {
            System.Diagnostics.Debug.WriteLine("Starting system tray initialization...");

            // Create TaskbarIcon first
            _taskbarIcon = new TaskbarIcon();

            // Set basic properties first
            _taskbarIcon.ToolTipText = "Wino Mail";

            // Configure the taskbar icon with icon loading
            var iconUri = new Uri("ms-appx:///Assets/Wino_Icon.ico");
            var bitmapImage = new BitmapImage(iconUri);
            _taskbarIcon.IconSource = bitmapImage;
            System.Diagnostics.Debug.WriteLine("Icon source set");

            // Create context menu
            var contextMenu = new MenuFlyout();

            // Show Window menu item
            var showMenuItem = new MenuFlyoutItem
            {
                Text = "Show Wino Mail",
                Icon = new SymbolIcon(Symbol.Home)
            };
            showMenuItem.Click += ShowMenuItem_Click;
            contextMenu.Items.Add(showMenuItem);
            System.Diagnostics.Debug.WriteLine("Show menu item added");

            // Separator
            contextMenu.Items.Add(new MenuFlyoutSeparator());

            // Exit menu item
            var exitMenuItem = new MenuFlyoutItem
            {
                Text = "Exit",
                Icon = new SymbolIcon(Symbol.Cancel)
            };
            exitMenuItem.Click += ExitMenuItem_Click;
            contextMenu.Items.Add(exitMenuItem);
            System.Diagnostics.Debug.WriteLine("Exit menu item added");

            // Set context menu
            _taskbarIcon.ContextFlyout = contextMenu;

            // Handle double-click using the proper event
            _taskbarIcon.LeftClickCommand = new RelayCommand(OnTrayIconLeftClick);

            // Set visibility and create explicitly 
            _taskbarIcon.Visibility = Visibility.Visible;

            // Try ForceCreate to ensure the icon is properly created in the system tray
            _taskbarIcon.ForceCreate();
            System.Diagnostics.Debug.WriteLine("System tray icon created and visible");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to initialize system tray: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }

    private void ShowMenuItem_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("Show menu item clicked");
        TrayIconDoubleClicked?.Invoke(this, EventArgs.Empty);
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("Exit menu item clicked");
        ExitApplication();
    }

    private void OnTrayIconLeftClick()
    {
        System.Diagnostics.Debug.WriteLine("Tray icon left clicked");
        TrayIconDoubleClicked?.Invoke(this, EventArgs.Empty);
    }

    public void Show()
    {
        if (_taskbarIcon != null)
        {
            try
            {
                _taskbarIcon.Visibility = Visibility.Visible;
                _taskbarIcon.ForceCreate(); // Ensure the icon is properly created and visible
                _isMinimizedToTray = true;
                System.Diagnostics.Debug.WriteLine("System tray icon set to visible and force created");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to show system tray icon: {ex.Message}");
            }
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("TaskbarIcon is null when trying to show");
        }
    }

    public void Hide()
    {
        if (_taskbarIcon != null)
        {
            _taskbarIcon.Visibility = Visibility.Collapsed;
            _isMinimizedToTray = false;
        }
    }

    private void ExitApplication()
    {
        System.Diagnostics.Debug.WriteLine("Attempting to exit application...");
        
        try
        {
            // Clean up the tray icon first
            Dispose();
            
            // Get the main window and close it properly
            if (WinoApplication.MainWindow is ShellWindow shellWindow)
            {
                // Force close the window without minimizing to tray
                shellWindow.ForceClose();
            }
            else
            {
                // Fallback to application exit
                Application.Current.Exit();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error during application exit: {ex.Message}");
            // Force exit if normal exit fails
            Environment.Exit(0);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        _taskbarIcon?.Dispose();
        _taskbarIcon = null;
        _isDisposed = true;
    }
}

// Simple RelayCommand implementation for the tray icon
public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
