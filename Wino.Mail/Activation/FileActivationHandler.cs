using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.UWP.Extensions;
using Wino.Views;

namespace Wino.Activation;

internal class FileActivationHandler : ActivationHandler<FileActivatedEventArgs>
{
    private readonly INativeAppService _nativeAppService;
    private readonly IMimeFileService _mimeFileService;
    private readonly IStatePersistanceService _statePersistanceService;
    private readonly INavigationService _winoNavigationService;

    public FileActivationHandler(INativeAppService nativeAppService,
                                 IMimeFileService mimeFileService,
                                 IStatePersistanceService statePersistanceService,
                                 INavigationService winoNavigationService)
    {
        _nativeAppService = nativeAppService;
        _mimeFileService = mimeFileService;
        _statePersistanceService = statePersistanceService;
        _winoNavigationService = winoNavigationService;
    }

    protected override async Task HandleInternalAsync(FileActivatedEventArgs args)
    {
        // Always handle the last item passed.
        // Multiple files are not supported.

        var file = args.Files.Last() as StorageFile;

        // Only EML files are supported now.
        var fileExtension = Path.GetExtension(file.Path);

        if (string.Equals(fileExtension, ".eml", StringComparison.OrdinalIgnoreCase))
        {
            var fileBytes = await file.ToByteArrayAsync();
            var directoryName = Path.GetDirectoryName(file.Path);

            var messageInformation = await _mimeFileService.GetMimeMessageInformationAsync(fileBytes, directoryName).ConfigureAwait(false);

            if (_nativeAppService.IsAppRunning())
            {
                // TODO: Activate another Window and go to mail rendering page.
                _winoNavigationService.Navigate(WinoPage.MailRenderingPage, messageInformation, NavigationReferenceFrame.RenderingFrame);
            }
            else
            {
                _statePersistanceService.ShouldShiftMailRenderingDesign = true;
                (Window.Current.Content as Frame).Navigate(typeof(MailRenderingPage), messageInformation, new DrillInNavigationTransitionInfo());
            }
        }
    }

    protected override bool CanHandleInternal(FileActivatedEventArgs args) => args.Files.Any();

}
