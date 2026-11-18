using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wino.Providers.Gmail.Google.Apis.Gmail.v1.Data;

public sealed class BatchDeleteMessagesRequest
{
    public IList<string> Ids { get; set; }
}

public sealed class BatchModifyMessagesRequest
{
    public IList<string> AddLabelIds { get; set; }

    public IList<string> Ids { get; set; }

    public IList<string> RemoveLabelIds { get; set; }
}

public sealed class Draft
{
    public string Id { get; set; }

    public Message Message { get; set; }
}

public sealed class History
{
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public ulong? Id { get; set; }

    public IList<HistoryLabelAdded> LabelsAdded { get; set; }

    public IList<HistoryLabelRemoved> LabelsRemoved { get; set; }

    public IList<Message> Messages { get; set; }

    public IList<HistoryMessageAdded> MessagesAdded { get; set; }

    public IList<HistoryMessageDeleted> MessagesDeleted { get; set; }
}

public sealed class HistoryLabelAdded
{
    public IList<string> LabelIds { get; set; }

    public Message Message { get; set; }
}

public sealed class HistoryLabelRemoved
{
    public IList<string> LabelIds { get; set; }

    public Message Message { get; set; }
}

public sealed class HistoryMessageAdded
{
    public Message Message { get; set; }
}

public sealed class HistoryMessageDeleted
{
    public Message Message { get; set; }
}

public sealed class Label
{
    public LabelColor Color { get; set; }

    public string Id { get; set; }

    public string LabelListVisibility { get; set; }

    public string MessageListVisibility { get; set; }

    public long? MessagesTotal { get; set; }

    public long? MessagesUnread { get; set; }

    public string Name { get; set; }

    public long? ThreadsTotal { get; set; }

    public long? ThreadsUnread { get; set; }

    public string Type { get; set; }
}

public sealed class LabelColor
{
    public string BackgroundColor { get; set; }

    public string TextColor { get; set; }
}

public sealed class ListDraftsResponse
{
    public IList<Draft> Drafts { get; set; }

    public string NextPageToken { get; set; }

    public long? ResultSizeEstimate { get; set; }
}

public sealed class ListHistoryResponse
{
    public IList<History> History { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public ulong? HistoryId { get; set; }

    public string NextPageToken { get; set; }
}

public sealed class ListLabelsResponse
{
    public IList<Label> Labels { get; set; }
}

public sealed class Filter
{
    public FilterAction Action { get; set; }

    public FilterCriteria Criteria { get; set; }

    public string Id { get; set; }
}

public sealed class FilterAction
{
    public IList<string> AddLabelIds { get; set; }

    public string Forward { get; set; }

    public IList<string> RemoveLabelIds { get; set; }
}

public sealed class FilterCriteria
{
    public bool? ExcludeChats { get; set; }

    public string From { get; set; }

    public bool? HasAttachment { get; set; }

    public string NegatedQuery { get; set; }

    public string Query { get; set; }

    public long? Size { get; set; }

    public string SizeComparison { get; set; }

    public string Subject { get; set; }

    public string To { get; set; }
}

public sealed class ListFiltersResponse
{
    [JsonPropertyName("filter")]
    public IList<Filter> Filter { get; set; }
}

public sealed class ListMessagesResponse
{
    public IList<Message> Messages { get; set; }

    public string NextPageToken { get; set; }

    public long? ResultSizeEstimate { get; set; }
}

public sealed class ListSendAsResponse
{
    public IList<SendAs> SendAs { get; set; }
}

public sealed class Message
{
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public ulong? HistoryId { get; set; }

    public string Id { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? InternalDate { get; set; }

    public IList<string> LabelIds { get; set; }

    public MessagePart Payload { get; set; }

    public string Raw { get; set; }

    public int? SizeEstimate { get; set; }

    public string Snippet { get; set; }

    public string ThreadId { get; set; }
}

public sealed class MessagePart
{
    public MessagePartBody Body { get; set; }

    public string Filename { get; set; }

    public IList<MessagePartHeader> Headers { get; set; }

    public string MimeType { get; set; }

    public string PartId { get; set; }

    public IList<MessagePart> Parts { get; set; }
}

public sealed class MessagePartBody
{
    public string AttachmentId { get; set; }

    public string Data { get; set; }

    public int? Size { get; set; }
}

public sealed class MessagePartHeader
{
    public string Name { get; set; }

    public string Value { get; set; }
}

/// <summary>Body of <c>users.watch</c>.</summary>
public sealed class WatchRequestBody
{
    public string TopicName { get; set; }

    public List<string> LabelIds { get; set; }

    /// <summary><c>INCLUDE</c> or <c>EXCLUDE</c>.</summary>
    public string LabelFilterBehavior { get; set; }
}

/// <summary>Response of <c>users.watch</c>. <c>Expiration</c> is epoch milliseconds.</summary>
public sealed class WatchResponse
{
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public ulong? HistoryId { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? Expiration { get; set; }
}

public sealed class Profile
{
    public string EmailAddress { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public ulong? HistoryId { get; set; }

    public long? MessagesTotal { get; set; }

    public long? ThreadsTotal { get; set; }
}

public sealed class SendAs
{
    public string DisplayName { get; set; }

    public bool? IsDefault { get; set; }

    public bool? IsPrimary { get; set; }

    public string ReplyToAddress { get; set; }

    public string SendAsEmail { get; set; }

    public string Signature { get; set; }

    public string VerificationStatus { get; set; }
}
