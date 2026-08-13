using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Google.Apis.Gmail.v1.Data;
using Wino.Core.Google;

namespace Wino.Core.Google
{
    public sealed class GmailService : IDisposable, IGoogleBatchService
    {
        public GmailService(HttpClient httpClient)
        {
            HttpClient = httpClient;
            Users = new global::Google.Apis.Gmail.v1.UsersResource(httpClient, this);
        }

        public global::Google.Apis.Gmail.v1.UsersResource Users { get; }

        public HttpClient HttpClient { get; }

        public string BatchEndpoint => "https://gmail.googleapis.com/batch/gmail/v1";

        public void Dispose()
        {
        }
    }
}

namespace Google.Apis.Gmail.v1
{
    public sealed class UsersResource
    {
        private const string BaseUri = "https://gmail.googleapis.com/gmail/v1/users";
        private readonly HttpClient _httpClient;
        private readonly object _service;

        internal UsersResource(HttpClient httpClient, object service)
        {
            _httpClient = httpClient;
            _service = service;
            Drafts = new DraftsResource(httpClient, service);
            History = new HistoryResource(httpClient, service);
            Labels = new LabelsResource(httpClient, service);
            Messages = new MessagesResource(httpClient, service);
            Settings = new SettingsResource(httpClient, service);
        }

        public DraftsResource Drafts { get; }

        public HistoryResource History { get; }

        public LabelsResource Labels { get; }

        public MessagesResource Messages { get; }

        public SettingsResource Settings { get; }

        public GetProfileRequest GetProfile(string userId) => new(_httpClient, _service, userId);

        public sealed class GetProfileRequest : GoogleApiRequest<Profile>
        {
            internal GetProfileRequest(HttpClient httpClient, object service, string userId)
                : base(
                    httpClient,
                    service,
                    HttpMethod.Get,
                    () => $"{BaseUri}/{GoogleUrl.Segment(userId)}/profile",
                    GoogleApiJsonContext.Default.Profile)
            {
            }
        }

        public sealed class DraftsResource
        {
            private readonly HttpClient _httpClient;
            private readonly object _service;

            internal DraftsResource(HttpClient httpClient, object service)
            {
                _httpClient = httpClient;
                _service = service;
            }

            public CreateRequest Create(Draft body, string userId) => new(_httpClient, _service, body, userId);

            public DeleteRequest Delete(string userId, string draftId) => new(_httpClient, _service, userId, draftId);

            public ListRequest List(string userId) => new(_httpClient, _service, userId);

            public SendRequest Send(Draft body, string userId) => new(_httpClient, _service, body, userId);

            public sealed class CreateRequest : GoogleApiRequest<Draft>
            {
                internal CreateRequest(HttpClient httpClient, object service, Draft body, string userId)
                    : base(
                        httpClient,
                        service,
                        HttpMethod.Post,
                        () => $"{BaseUri}/{GoogleUrl.Segment(userId)}/drafts",
                        GoogleApiJsonContext.Default.Draft,
                        () => GoogleJsonContent.Create(body, GoogleApiJsonContext.Default.Draft))
                {
                }
            }

            public sealed class DeleteRequest : GoogleApiRequest<GoogleEmptyResponse>
            {
                internal DeleteRequest(HttpClient httpClient, object service, string userId, string draftId)
                    : base(
                        httpClient,
                        service,
                        HttpMethod.Delete,
                        () => $"{BaseUri}/{GoogleUrl.Segment(userId)}/drafts/{GoogleUrl.Segment(draftId)}",
                        GoogleApiJsonContext.Default.GoogleEmptyResponse)
                {
                }
            }

            public sealed class ListRequest : GoogleApiRequest<ListDraftsResponse>
            {
                internal ListRequest(HttpClient httpClient, object service, string userId)
                    : base(
                        httpClient,
                        service,
                        HttpMethod.Get,
                        () => $"{BaseUri}/{GoogleUrl.Segment(userId)}/drafts",
                        GoogleApiJsonContext.Default.ListDraftsResponse)
                {
                }
            }

            public sealed class SendRequest : GoogleApiRequest<Message>
            {
                internal SendRequest(HttpClient httpClient, object service, Draft body, string userId)
                    : base(
                        httpClient,
                        service,
                        HttpMethod.Post,
                        () => $"{BaseUri}/{GoogleUrl.Segment(userId)}/drafts/send",
                        GoogleApiJsonContext.Default.Message,
                        () => GoogleJsonContent.Create(body, GoogleApiJsonContext.Default.Draft))
                {
                }
            }
        }

        public sealed class HistoryResource
        {
            private readonly HttpClient _httpClient;
            private readonly object _service;

            internal HistoryResource(HttpClient httpClient, object service)
            {
                _httpClient = httpClient;
                _service = service;
            }

            public ListRequest List(string userId) => new(_httpClient, _service, userId);

            public sealed class ListRequest : GoogleApiRequest<ListHistoryResponse>
            {
                private readonly string _userId;

                internal ListRequest(HttpClient httpClient, object service, string userId)
                    : base(
                        httpClient,
                        service,
                        HttpMethod.Get,
                        () => string.Empty,
                        GoogleApiJsonContext.Default.ListHistoryResponse)
                {
                    _userId = userId;
                    SetRequestUriFactory();
                }

                public string PageToken { get; set; }

                public ulong? StartHistoryId { get; set; }

                private void SetRequestUriFactory()
                {
                    RequestUriFactory = () => GoogleUrl.AddQuery(
                        $"{BaseUri}/{GoogleUrl.Segment(_userId)}/history",
                        ("startHistoryId", GoogleUrl.Number(StartHistoryId)),
                        ("pageToken", PageToken));
                }
            }
        }

        public sealed class LabelsResource
        {
            private readonly HttpClient _httpClient;
            private readonly object _service;

            internal LabelsResource(HttpClient httpClient, object service)
            {
                _httpClient = httpClient;
                _service = service;
            }

            public CreateRequest Create(Label body, string userId) => new(_httpClient, _service, body, userId);

            public DeleteRequest Delete(string userId, string id) => new(_httpClient, _service, userId, id);

            public GetRequest Get(string userId, string id) => new(_httpClient, _service, userId, id);

            public ListRequest List(string userId) => new(_httpClient, _service, userId);

            public UpdateRequest Update(Label body, string userId, string id) => new(_httpClient, _service, body, userId, id);

            public sealed class CreateRequest : GoogleApiRequest<Label>
            {
                internal CreateRequest(HttpClient httpClient, object service, Label body, string userId)
                    : base(
                        httpClient,
                        service,
                        HttpMethod.Post,
                        () => $"{BaseUri}/{GoogleUrl.Segment(userId)}/labels",
                        GoogleApiJsonContext.Default.Label,
                        () => GoogleJsonContent.Create(body, GoogleApiJsonContext.Default.Label))
                {
                }
            }

            public sealed class DeleteRequest : GoogleApiRequest<GoogleEmptyResponse>
            {
                internal DeleteRequest(HttpClient httpClient, object service, string userId, string id)
                    : base(
                        httpClient,
                        service,
                        HttpMethod.Delete,
                        () => $"{BaseUri}/{GoogleUrl.Segment(userId)}/labels/{GoogleUrl.Segment(id)}",
                        GoogleApiJsonContext.Default.GoogleEmptyResponse)
                {
                }
            }

            public sealed class GetRequest : GoogleApiRequest<Label>
            {
                internal GetRequest(HttpClient httpClient, object service, string userId, string id)
                    : base(
                        httpClient,
                        service,
                        HttpMethod.Get,
                        () => $"{BaseUri}/{GoogleUrl.Segment(userId)}/labels/{GoogleUrl.Segment(id)}",
                        GoogleApiJsonContext.Default.Label)
                {
                }
            }

            public sealed class ListRequest : GoogleApiRequest<ListLabelsResponse>
            {
                internal ListRequest(HttpClient httpClient, object service, string userId)
                    : base(
                        httpClient,
                        service,
                        HttpMethod.Get,
                        () => $"{BaseUri}/{GoogleUrl.Segment(userId)}/labels",
                        GoogleApiJsonContext.Default.ListLabelsResponse)
                {
                }
            }

            public sealed class UpdateRequest : GoogleApiRequest<Label>
            {
                internal UpdateRequest(HttpClient httpClient, object service, Label body, string userId, string id)
                    : base(
                        httpClient,
                        service,
                        HttpMethod.Put,
                        () => $"{BaseUri}/{GoogleUrl.Segment(userId)}/labels/{GoogleUrl.Segment(id)}",
                        GoogleApiJsonContext.Default.Label,
                        () => GoogleJsonContent.Create(body, GoogleApiJsonContext.Default.Label))
                {
                }
            }
        }

        public sealed class MessagesResource
        {
            private readonly HttpClient _httpClient;
            private readonly object _service;

            internal MessagesResource(HttpClient httpClient, object service)
            {
                _httpClient = httpClient;
                _service = service;
                Attachments = new AttachmentsResource(httpClient, service);
            }

            public AttachmentsResource Attachments { get; }

            public BatchDeleteRequest BatchDelete(BatchDeleteMessagesRequest body, string userId)
                => new(_httpClient, _service, body, userId);

            public BatchModifyRequest BatchModify(BatchModifyMessagesRequest body, string userId)
                => new(_httpClient, _service, body, userId);

            public GetRequest Get(string userId, string id) => new(_httpClient, _service, userId, id);

            public ListRequest List(string userId) => new(_httpClient, _service, userId);

            public sealed class BatchDeleteRequest : GoogleApiRequest<GoogleEmptyResponse>
            {
                internal BatchDeleteRequest(HttpClient httpClient, object service, BatchDeleteMessagesRequest body, string userId)
                    : base(
                        httpClient,
                        service,
                        HttpMethod.Post,
                        () => $"{BaseUri}/{GoogleUrl.Segment(userId)}/messages/batchDelete",
                        GoogleApiJsonContext.Default.GoogleEmptyResponse,
                        () => GoogleJsonContent.Create(body, GoogleApiJsonContext.Default.BatchDeleteMessagesRequest))
                {
                }
            }

            public sealed class BatchModifyRequest : GoogleApiRequest<GoogleEmptyResponse>
            {
                internal BatchModifyRequest(HttpClient httpClient, object service, BatchModifyMessagesRequest body, string userId)
                    : base(
                        httpClient,
                        service,
                        HttpMethod.Post,
                        () => $"{BaseUri}/{GoogleUrl.Segment(userId)}/messages/batchModify",
                        GoogleApiJsonContext.Default.GoogleEmptyResponse,
                        () => GoogleJsonContent.Create(body, GoogleApiJsonContext.Default.BatchModifyMessagesRequest))
                {
                }
            }

            public sealed class GetRequest : GoogleApiRequest<Message>
            {
                private readonly string _id;
                private readonly string _userId;

                internal GetRequest(HttpClient httpClient, object service, string userId, string id)
                    : base(
                        httpClient,
                        service,
                        HttpMethod.Get,
                        () => string.Empty,
                        GoogleApiJsonContext.Default.Message)
                {
                    _userId = userId;
                    _id = id;
                    RequestUriFactory = () => GoogleUrl.AddQuery(
                        $"{BaseUri}/{GoogleUrl.Segment(_userId)}/messages/{GoogleUrl.Segment(_id)}",
                        ("format", Format.ToString().ToLowerInvariant()),
                        ("fields", Fields));
                }

                public FormatEnum Format { get; set; } = FormatEnum.Full;

                public string Fields { get; set; }

                public enum FormatEnum
                {
                    Full,
                    Metadata,
                    Minimal,
                    Raw
                }
            }

            public sealed class AttachmentsResource
            {
                private readonly HttpClient _httpClient;
                private readonly object _service;

                internal AttachmentsResource(HttpClient httpClient, object service)
                {
                    _httpClient = httpClient;
                    _service = service;
                }

                public GetRequest Get(string userId, string messageId, string id)
                    => new(_httpClient, _service, userId, messageId, id);

                public sealed class GetRequest : GoogleApiRequest<MessagePartBody>
                {
                    internal GetRequest(HttpClient httpClient, object service, string userId, string messageId, string id)
                        : base(
                            httpClient,
                            service,
                            HttpMethod.Get,
                            () => GoogleUrl.AddQuery(
                                $"{BaseUri}/{GoogleUrl.Segment(userId)}/messages/{GoogleUrl.Segment(messageId)}/attachments/{GoogleUrl.Segment(id)}",
                                ("fields", "data")),
                            GoogleApiJsonContext.Default.MessagePartBody)
                    {
                    }
                }
            }

            public sealed class ListRequest : GoogleApiRequest<ListMessagesResponse>
            {
                private readonly string _userId;

                internal ListRequest(HttpClient httpClient, object service, string userId)
                    : base(
                        httpClient,
                        service,
                        HttpMethod.Get,
                        () => string.Empty,
                        GoogleApiJsonContext.Default.ListMessagesResponse)
                {
                    _userId = userId;
                    RequestUriFactory = BuildRequestUri;
                }

                public bool? IncludeSpamTrash { get; set; }

                public IList<string> LabelIds { get; set; }

                public long? MaxResults { get; set; }

                public string PageToken { get; set; }

                public string Q { get; set; }

                private string BuildRequestUri()
                {
                    var parameters = new List<(string Name, string Value)>
                    {
                        ("includeSpamTrash", GoogleUrl.Boolean(IncludeSpamTrash)),
                        ("maxResults", GoogleUrl.Number(MaxResults)),
                        ("pageToken", PageToken),
                        ("q", Q)
                    };

                    if (LabelIds != null)
                        parameters.AddRange(LabelIds.Select(id => ("labelIds", id)));

                    return GoogleUrl.AddRepeatedQuery(
                        $"{BaseUri}/{GoogleUrl.Segment(_userId)}/messages",
                        parameters);
                }
            }
        }

        public sealed class SettingsResource
        {
            internal SettingsResource(HttpClient httpClient, object service)
            {
                Filters = new FiltersResource(httpClient, service);
                SendAs = new SendAsResource(httpClient, service);
            }

            public FiltersResource Filters { get; }

            public SendAsResource SendAs { get; }

            public sealed class FiltersResource
            {
                private readonly HttpClient _httpClient;
                private readonly object _service;

                internal FiltersResource(HttpClient httpClient, object service)
                {
                    _httpClient = httpClient;
                    _service = service;
                }

                public ListRequest List(string userId) => new(_httpClient, _service, userId);

                public GetRequest Get(string userId, string id) => new(_httpClient, _service, userId, id);

                public CreateRequest Create(Filter body, string userId)
                    => new(_httpClient, _service, body, userId);

                public DeleteRequest Delete(string userId, string id)
                    => new(_httpClient, _service, userId, id);

                public sealed class ListRequest : GoogleApiRequest<ListFiltersResponse>
                {
                    internal ListRequest(HttpClient httpClient, object service, string userId)
                        : base(
                            httpClient,
                            service,
                            HttpMethod.Get,
                            () => $"{BaseUri}/{GoogleUrl.Segment(userId)}/settings/filters",
                            GoogleApiJsonContext.Default.ListFiltersResponse)
                    {
                    }
                }

                public sealed class GetRequest : GoogleApiRequest<Filter>
                {
                    internal GetRequest(HttpClient httpClient, object service, string userId, string id)
                        : base(
                            httpClient,
                            service,
                            HttpMethod.Get,
                            () => $"{BaseUri}/{GoogleUrl.Segment(userId)}/settings/filters/{GoogleUrl.Segment(id)}",
                            GoogleApiJsonContext.Default.Filter)
                    {
                    }
                }

                public sealed class CreateRequest : GoogleApiRequest<Filter>
                {
                    internal CreateRequest(HttpClient httpClient, object service, Filter body, string userId)
                        : base(
                            httpClient,
                            service,
                            HttpMethod.Post,
                            () => $"{BaseUri}/{GoogleUrl.Segment(userId)}/settings/filters",
                            GoogleApiJsonContext.Default.Filter,
                            () => GoogleJsonContent.Create(body, GoogleApiJsonContext.Default.Filter))
                    {
                    }
                }

                public sealed class DeleteRequest : GoogleApiRequest<GoogleEmptyResponse>
                {
                    internal DeleteRequest(HttpClient httpClient, object service, string userId, string id)
                        : base(
                            httpClient,
                            service,
                            HttpMethod.Delete,
                            () => $"{BaseUri}/{GoogleUrl.Segment(userId)}/settings/filters/{GoogleUrl.Segment(id)}",
                            GoogleApiJsonContext.Default.GoogleEmptyResponse)
                    {
                    }
                }
            }

            public sealed class SendAsResource
            {
                private readonly HttpClient _httpClient;
                private readonly object _service;

                internal SendAsResource(HttpClient httpClient, object service)
                {
                    _httpClient = httpClient;
                    _service = service;
                }

                public ListRequest List(string userId) => new(_httpClient, _service, userId);

                public sealed class ListRequest : GoogleApiRequest<ListSendAsResponse>
                {
                    internal ListRequest(HttpClient httpClient, object service, string userId)
                        : base(
                            httpClient,
                            service,
                            HttpMethod.Get,
                            () => $"{BaseUri}/{GoogleUrl.Segment(userId)}/settings/sendAs",
                            GoogleApiJsonContext.Default.ListSendAsResponse)
                    {
                    }
                }
            }
        }
    }
}
