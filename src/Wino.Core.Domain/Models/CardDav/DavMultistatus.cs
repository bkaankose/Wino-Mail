using System.Collections.Generic;

namespace Wino.Core.Domain.Models.CardDav;

public sealed class DavMultistatus
{
    public List<DavResponseItem> Responses { get; } = [];
    public string SyncToken { get; set; }
}

public sealed class DavResponseItem
{
    public string Href { get; set; }
    public int? StatusCode { get; set; }
    public List<DavPropertyStatus> PropertyStatuses { get; } = [];
    public List<string> ErrorNames { get; } = [];
}

public sealed class DavPropertyStatus
{
    public int? StatusCode { get; set; }
    public List<DavProperty> Properties { get; } = [];
    public List<string> ErrorNames { get; } = [];
}

public sealed class DavProperty
{
    public string Namespace { get; set; }
    public string Name { get; set; }
    public string Value { get; set; }
    public string Xml { get; set; }
}
