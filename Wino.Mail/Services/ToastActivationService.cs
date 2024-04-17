using Wino.Core.Domain.Interfaces;

namespace Wino.Services
{
    public class ToastActivationService
    {
        private readonly IMailService _mailService;
        private readonly IWinoRequestDelegator _winoRequestDelegator;
        private readonly INativeAppService _nativeAppService;

        public ToastActivationService(IMailService mailService,
                                      IWinoRequestDelegator winoRequestDelegator,
                                      INativeAppService nativeAppService)
        {
            _mailService = mailService;
            _winoRequestDelegator = winoRequestDelegator;
            _nativeAppService = nativeAppService;
        }
    }
}
