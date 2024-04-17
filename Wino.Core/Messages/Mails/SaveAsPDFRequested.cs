using System;
using System.Collections.Generic;
using System.Text;

namespace Wino.Core.Messages.Mails
{
    /// <summary>
    /// When mail save as PDF requested.
    /// </summary>
    public record SaveAsPDFRequested(string FileSavePath);
}
