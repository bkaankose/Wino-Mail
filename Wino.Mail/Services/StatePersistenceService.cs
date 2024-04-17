using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.AppCenter.Crashes;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Messages.Shell;

namespace Wino.Services
{
    public class StatePersistenceService : ObservableObject, IStatePersistanceService
    {
        public event EventHandler<string> StatePropertyChanged;

        private const string OpenPaneLengthKey = nameof(OpenPaneLengthKey);
        private const string MailListPaneLengthKey = nameof(MailListPaneLengthKey);

        private readonly IConfigurationService _configurationService;

        public StatePersistenceService(IConfigurationService configurationService)
        {
            _configurationService = configurationService;

            openPaneLength = _configurationService.Get(OpenPaneLengthKey, 320d);
            _mailListPaneLength = _configurationService.Get(MailListPaneLengthKey, 420d);

            PropertyChanged += ServicePropertyChanged;
        }

        private void ServicePropertyChanged(object sender, PropertyChangedEventArgs e) => StatePropertyChanged?.Invoke(this, e.PropertyName);

        public bool IsBackButtonVisible => IsReadingMail && IsReaderNarrowed;

        private bool isReadingMail;

        public bool IsReadingMail
        {
            get => isReadingMail;
            set
            {
                if (SetProperty(ref isReadingMail, value))
                {
                    OnPropertyChanged(nameof(IsBackButtonVisible));
                    WeakReferenceMessenger.Default.Send(new ShellStateUpdated());
                }
            }
        }

        private bool shouldShiftMailRenderingDesign;

        public bool ShouldShiftMailRenderingDesign
        {
            get { return shouldShiftMailRenderingDesign; }
            set { shouldShiftMailRenderingDesign = value; }
        }

        private bool isReaderNarrowed;

        public bool IsReaderNarrowed
        {
            get => isReaderNarrowed;
            set
            {
                if (SetProperty(ref isReaderNarrowed, value))
                {
                    OnPropertyChanged(nameof(IsBackButtonVisible));
                    WeakReferenceMessenger.Default.Send(new ShellStateUpdated());
                }
            }
        }

        private string coreWindowTitle;

        public string CoreWindowTitle
        {
            get => coreWindowTitle;
            set
            {
                if (SetProperty(ref coreWindowTitle, value))
                {
                    UpdateAppCoreWindowTitle();
                }
            }
        }

        #region Settings

        private double openPaneLength;
        public double OpenPaneLength
        {
            get => openPaneLength;
            set
            {
                if (SetProperty(ref openPaneLength, value))
                {
                    _configurationService.Set(OpenPaneLengthKey, value);
                }
            }
        }

        private double _mailListPaneLength;
        public double MailListPaneLength
        {
            get => _mailListPaneLength;
            set
            {
                if (SetProperty(ref _mailListPaneLength, value))
                {
                    _configurationService.Set(MailListPaneLengthKey, value);
                }
            }

        }

        #endregion

        private void UpdateAppCoreWindowTitle()
        {
            try
            {
                var appView = Windows.UI.ViewManagement.ApplicationView.GetForCurrentView();

                if (appView != null)
                    appView.Title = CoreWindowTitle;
            }
            catch (System.Exception ex)
            {
                Crashes.TrackError(ex);
            }
        }
    }
}
