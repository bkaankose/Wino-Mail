using System;
using System.ComponentModel;
using CommunityToolkit.WinUI;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Windows.UI.Xaml;

namespace Wino.Controls;

/// <summary>
/// UWP contact avatar backed by WinUI 2 PersonPicture. The former desktop image
/// decoding pipeline is intentionally not used in the sandboxed UI process.
/// </summary>
public sealed partial class ImagePreviewControl : Microsoft.UI.Xaml.Controls.PersonPicture, IDisposable
{
    private INotifyPropertyChanged? _mailItemSource;

    [GeneratedDependencyProperty]
    public partial IMailItemDisplayInformation? MailItemInformation { get; set; }

    [GeneratedDependencyProperty]
    public partial AccountContact? PreviewContact { get; set; }

    [GeneratedDependencyProperty]
    public partial string? Address { get; set; }

    [GeneratedDependencyProperty]
    public partial string? DisplayNameOverride { get; set; }

    public ImagePreviewControl()
    {
        DefaultStyleKey = typeof(Microsoft.UI.Xaml.Controls.PersonPicture);
        IsTabStop = false;
    }

    partial void OnMailItemInformationPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        if (_mailItemSource is not null)
        {
            _mailItemSource.PropertyChanged -= MailItemSourcePropertyChanged;
        }

        _mailItemSource = e.NewValue as INotifyPropertyChanged;
        if (_mailItemSource is not null)
        {
            _mailItemSource.PropertyChanged += MailItemSourcePropertyChanged;
        }

        RefreshIdentity();
    }

    partial void OnPreviewContactPropertyChanged(DependencyPropertyChangedEventArgs e) => RefreshIdentity();
    partial void OnAddressPropertyChanged(DependencyPropertyChangedEventArgs e) => RefreshIdentity();
    partial void OnDisplayNameOverridePropertyChanged(DependencyPropertyChangedEventArgs e) => RefreshIdentity();

    private void MailItemSourcePropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshIdentity();

    private void RefreshIdentity()
    {
        var address = PreviewContact?.Address ?? Address ?? MailItemInformation?.SenderContact?.Address ?? MailItemInformation?.FromAddress;
        DisplayName = PreviewContact?.Name ?? DisplayNameOverride ?? MailItemInformation?.SenderContact?.Name ?? MailItemInformation?.FromName ?? address ?? string.Empty;
    }

    public void Dispose()
    {
        if (_mailItemSource is not null)
        {
            _mailItemSource.PropertyChanged -= MailItemSourcePropertyChanged;
            _mailItemSource = null;
        }
    }
}
