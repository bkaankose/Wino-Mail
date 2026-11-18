using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wino.Providers.Gmail.Google.Apis.Drive.v3.Data;

public sealed class File
{
    public string Id { get; set; }

    public string MimeType { get; set; }

    public string Name { get; set; }

    public IList<string> Parents { get; set; }

    public string WebViewLink { get; set; }
}
