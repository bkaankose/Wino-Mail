using System.Net;
using System.Text;
using System.Xml.Linq;
using Wino.Core.Synchronizers.Eas;

internal static class ProtocolTests
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            count++;
        }
        void Invalid(byte[] bytes)
        {
            try { EasWbxml.Decode(bytes); throw new Exception("Malformed WBXML was accepted."); }
            catch (InvalidDataException) { count++; }
        }
        XNamespace sync = "AirSync:";
        XNamespace folder = "FolderHierarchy:";
        XNamespace body = "AirSyncBase:";
        var xml = new XElement(sync + "Sync", new XElement(sync + "Collections", new XElement(sync + "Collection",
            new XElement(sync + "SyncKey", "0"), new XElement(sync + "CollectionId", "日本語 & è"),
            new XElement(sync + "ApplicationData", new XElement(body + "Body", new XElement(body + "Type", "1"), new XElement(body + "Data", "hello <world>"))))));
        Check(XNode.DeepEquals(xml, EasWbxml.Decode(EasWbxml.Encode(xml))), "Namespaces and Unicode round trip");
        Check(Convert.ToHexString(EasWbxml.Encode(new XElement(sync + "Sync"))) == "03016A0005", "Published WBXML token/header vector");
        var opaque = new byte[] { 0, 0xFF, 0x80, 0xA0, 0xFE, 0x0D, 0x0A };
        var opaqueXml = new XElement(body + "Data", new XAttribute(EasWbxml.Opaque, "base64"), Convert.ToBase64String(opaque));
        var opaqueDecoded = EasWbxml.Decode(EasWbxml.Encode(opaqueXml));
        Check(opaque.SequenceEqual(Convert.FromBase64String(opaqueDecoded.Value)), "Binary opaque content preserved");
        Invalid(Array.Empty<byte>());
        Invalid(new byte[] { 3, 1, 106, 0, 0x45 });
        Invalid(new byte[] { 3, 1, 106, 0, 1 });
        Invalid(new byte[] { 3, 1, 106, 0, 0x45, 0xC3, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F });
        Invalid(new byte[] { 3, 1, 106, 0, 5, 5 });
        Invalid(new byte[] { 3, 1, 106, 0, 0, 255, 5 });
        Check(EasDataMapper.Key(Guid.Empty, "a/b", "c") != EasDataMapper.Key(Guid.Empty, "a", "b/c"), "Opaque server IDs cannot collide");
        Check(EasDataMapper.Date("20261104T103000Z") == new DateTime(2026, 11, 4, 10, 30, 0, DateTimeKind.Utc), "EAS timestamp parsed in UTC");
        foreach (var zoneId in new[] { "UTC", "W. Europe Standard Time", "Eastern Standard Time", "AUS Eastern Standard Time" })
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
            var data = new XElement(sync + "ApplicationData", new XElement(EasDataMapper.Calendar + "TimeZone", EasTimeZone.Write(zone, 2026)));
            var read = EasTimeZone.Read(data, 2026);
            Check(Enumerable.Range(0, 365 * 24).All(hour => zone.GetUtcOffset(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(hour)) == read.GetUtcOffset(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(hour))), "Timezone " + zoneId);
        }

        var sent = new List<string>();
        var accountId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        using (var client = new EasClient("https://eas.example.test", "DOMAIN\\person", "synthetic-password", accountId,
            new Handler(async request =>
            {
                Check(request.RequestUri!.Scheme == "https", "HTTPS transport");
                Check(request.Headers.Authorization?.Scheme == "Basic", "Basic auth selected");
                if (request.Method == HttpMethod.Options)
                {
                    var options = new HttpResponseMessage(HttpStatusCode.OK);
                    options.Headers.Add("MS-ASProtocolVersions", "12.1,14.0,14.1,16.1");
                    return options;
                }
                Check(request.Headers.GetValues("MS-ASProtocolVersion").Single() == "14.1", "Negotiated supported version");
                Check(request.RequestUri.AbsolutePath == "/Microsoft-Server-ActiveSync", "Default endpoint path");
                var doc = EasWbxml.Decode(await request.Content!.ReadAsByteArrayAsync());
                sent.Add(doc.Name.LocalName);
                if (doc.Name.LocalName == "FolderSync")
                    return Response(new XElement(folder + "FolderSync", new XElement(folder + "Status", "1"), new XElement(folder + "SyncKey", "folders-1"), new XElement(folder + "Changes")));
                if (doc.Name.LocalName == "Sync")
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                if (doc.Name.LocalName == "SendMail")
                {
                    XNamespace compose = "ComposeMail:";
                    Check(opaque.SequenceEqual(Convert.FromBase64String(doc.Element(compose + "Mime")!.Value)), "SendMail uses opaque MIME bytes");
                    Check(doc.Element(compose + "ClientId")!.Value == "stable-client-id", "Stable send identifier");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                }
                throw new InvalidOperationException("Unexpected command");
            })))
        {
            await client.FolderSyncAsync("0", CancellationToken.None);
            var idle = await client.SyncAsync("2", "items-1", "Email", null, CancellationToken.None);
            Check(idle.Element(sync + "SyncKey")!.Value == "items-1", "Empty sync retains key");
            await client.SendMailAsync(opaque, "stable-client-id", CancellationToken.None);
        }
        Check(sent.SequenceEqual(new[] { "FolderSync", "Sync", "SendMail" }), "No extra requests or send retries");

        var unsupportedCalls = 0;
        using (var unsupported = new EasClient("https://eas.example.test", "person", "synthetic-password", accountId, new Handler(request =>
        {
            unsupportedCalls++;
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Add("MS-ASProtocolVersions", "2.5");
            return Task.FromResult(response);
        })))
        {
            try { await unsupported.ConnectAsync(CancellationToken.None); throw new Exception("Unsupported EAS versions were accepted."); }
            catch (NotSupportedException exception)
            {
                Check(exception.Message.Contains("2.5") &&
                    exception.Message.Contains("Wino supports EAS 12.0, 12.1, 14.0 and 14.1") && unsupportedCalls == 1,
                    "Version mismatch reports server and client versions without sending commands");
            }
        }

        var calls = 0;
        using (var rejected = new EasClient("https://eas.example.test", "person", "synthetic-password", accountId, new Handler(request =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://other.example.test") } });
        })))
        {
            try { await rejected.ConnectAsync(CancellationToken.None); throw new Exception("Redirect was accepted."); }
            catch (HttpRequestException exception) { Check(exception.StatusCode == HttpStatusCode.TemporaryRedirect && calls == 1, "Redirect never receives credentials"); }
        }
        try { using var invalid = new EasClient("http://eas.example.test", "person", "password", accountId); throw new Exception("HTTP was accepted."); }
        catch (ArgumentException) { count++; }

        using (var message = new MimeKit.MimeMessage())
        {
            message.From.Add(new MimeKit.MailboxAddress("Sender", "sender@example.test"));
            message.To.Add(new MimeKit.MailboxAddress("Recipient", "recipient@example.test"));
            message.Subject = "日本語 è";
            var builder = new MimeKit.BodyBuilder { TextBody = "A message with a binary attachment." };
            builder.Attachments.Add("payload.bin", opaque);
            message.Body = builder.ToMessageBody();
            using var encoded = new MemoryStream();
            message.WriteTo(encoded);
            var document = new XElement(sync + "ApplicationData", new XElement(body + "Body", new XElement(body + "Type", "4"),
                new XElement(body + "Data", new XAttribute(EasWbxml.Opaque, "base64"), Convert.ToBase64String(encoded.ToArray()))));
            using var decoded = EasDataMapper.ReadMime(EasWbxml.Decode(EasWbxml.Encode(document)));
            using var attachment = new MemoryStream();
            (((MimeKit.MimePart)decoded.Attachments.Single()).Content ?? throw new InvalidOperationException("MIME attachment content missing.")).DecodeTo(attachment);
            Check(decoded.Subject == message.Subject && opaque.SequenceEqual(attachment.ToArray()), "MIME attachment survives EAS WBXML");
        }
        var allDay = new Wino.Core.Domain.Entities.Calendar.CalendarItem
        {
            Id = Guid.NewGuid(),
            StartDate = new DateTime(2026, 3, 29),
            DurationInSeconds = 86400,
            StartTimeZone = "W. Europe Standard Time",
            EndTimeZone = "W. Europe Standard Time",
            Title = "All day"
        };
        var allDayXml = EasDataMapper.WriteEvent(allDay, Array.Empty<Wino.Core.Domain.Entities.Calendar.CalendarEventAttendee>(), Array.Empty<Wino.Core.Domain.Entities.Calendar.Reminder>());
        var clientEventId = Guid.NewGuid();
        var clientEventData = new XElement(sync + "ApplicationData", new XElement(EasDataMapper.Calendar + "UID", clientEventId.ToString("N")));
        Check(EasDataMapper.ClientEventId(EasWbxml.Decode(EasWbxml.Encode(clientEventData))) == clientEventId, "Client UID survives WBXML and identifies the optimistic event");
        clientEventData.Element(EasDataMapper.Calendar + "UID")!.Value = clientEventId.ToString("D");
        Check(EasDataMapper.ClientEventId(clientEventData) == clientEventId, "Server-normalized client UID retains the same identity");
        clientEventData.Element(EasDataMapper.Calendar + "UID")!.Value = "foreign-calendar-uid@example.test";
        Check(EasDataMapper.ClientEventId(clientEventData) == null, "Foreign calendar UID is not treated as a local event identifier");
        Check(allDayXml.Element(EasDataMapper.Calendar + "Recurrence") == null, "Non-recurring event omits the recurrence container required to contain Type in EAS 12.x/14.x");
        Check(allDayXml.Element(EasDataMapper.Calendar + "Reminder") == null, "Event without reminder omits the numeric Reminder field");
        var reminded = EasDataMapper.WriteEvent(allDay, Array.Empty<Wino.Core.Domain.Entities.Calendar.CalendarEventAttendee>(), new[] { new Wino.Core.Domain.Entities.Calendar.Reminder { DurationInSeconds = 900 } });
        Check(reminded.Element(EasDataMapper.Calendar + "Reminder")?.Value == "15", "Reminder duration is serialized as numeric minutes");
        Check(allDayXml.Element(EasDataMapper.Calendar + "TimeZone") == null, "All-day events omit timezone");
        Check(allDayXml.Element(EasDataMapper.Calendar + "StartTime")!.Value == "20260329T000000Z", "All-day events retain the calendar date over DST");
        var recurrence = new Wino.Core.Domain.Entities.Calendar.CalendarItem
        {
            Id = Guid.NewGuid(),
            StartDate = new DateTime(2026, 11, 4, 10, 30, 0),
            DurationInSeconds = 3600,
            StartTimeZone = "W. Europe Standard Time",
            EndTimeZone = "W. Europe Standard Time",
            Title = "Weekly",
            Recurrence = "RRULE:FREQ=WEEKLY;INTERVAL=1;BYDAY=WE"
        };
        var recurringXml = EasDataMapper.WriteEvent(recurrence, Array.Empty<Wino.Core.Domain.Entities.Calendar.CalendarEventAttendee>(), Array.Empty<Wino.Core.Domain.Entities.Calendar.Reminder>());
        var calendar = new Wino.Core.Domain.Entities.Calendar.AccountCalendar { Id = Guid.NewGuid(), AccountId = accountId, RemoteCalendarId = "8" };
        var recurring = EasDataMapper.ReadEvent(recurringXml, calendar, "event-1");
        Check(recurring.StartDate == recurrence.StartDate && recurring.Recurrence.Contains("BYDAY=WE"), "Zoned weekly recurrence round trip");

        var acknowledgements = 0;
        var provisioning = 0;
        var challenged = false;
        using (var provisioned = new EasClient("https://eas.example.test", "person", "synthetic-password", accountId,
            new Handler(async request =>
            {
                if (request.Method == HttpMethod.Options)
                {
                    var options = new HttpResponseMessage(HttpStatusCode.OK);
                    options.Headers.Add("MS-ASProtocolVersions", "14.1");
                    return options;
                }
                var doc = EasWbxml.Decode(await request.Content!.ReadAsByteArrayAsync());
                XNamespace policy = "Provision:";
                if (doc.Name.LocalName == "Provision")
                {
                    provisioning++;
                    var isAcknowledgement = doc.Element(policy + "Policies")!.Element(policy + "Policy")!.Element(policy + "Status") != null;
                    if (isAcknowledgement) acknowledgements++;
                    return Response(new XElement(policy + "Provision", new XElement(policy + "Status", "1"),
                        new XElement(policy + "Policies", new XElement(policy + "Policy", new XElement(policy + "Status", "1"),
                            new XElement(policy + "PolicyKey", isAcknowledgement ? "accepted-key" : "temporary-key"),
                            isAcknowledgement ? null : new XElement(policy + "Data", new XElement(policy + "EASProvisionDoc",
                                new XElement(policy + "DevicePasswordEnabled", "0"), new XElement(policy + "AttachmentsEnabled", "1"),
                                new XElement(policy + "AllowBluetooth", "2"), new XElement(policy + "RequireDeviceEncryption", "0")))))));
                }
                if (!challenged) { challenged = true; return new HttpResponseMessage((HttpStatusCode)449); }
                Check(request.Headers.GetValues("X-MS-PolicyKey").Single() == "accepted-key", "Accepted policy key used on replay");
                return Response(new XElement(folder + "FolderSync", new XElement(folder + "Status", "1"), new XElement(folder + "SyncKey", "folders-2"), new XElement(folder + "Changes")));
            })))
            await provisioned.FolderSyncAsync("0", CancellationToken.None);
        Check(provisioning == 2 && acknowledgements == 1, "Provisioning has exactly two phases and one replay");

        var flagData = EasDataMapper.WriteFlag(true).Elements().Single();
        XNamespace emailNs = "Email:";
        Check(flagData.Element(emailNs + "FlagType")!.Value == "Flag for follow up", "Flag includes required follow-up type");
        Check(new[] { "StartDate", "UtcStartDate", "DueDate", "UtcDueDate" }.All(name => flagData.Element(EasDataMapper.Tasks + name) is { Value.Length: 0 }), "Undated flag has all four NULL date fields");
        var desiredData = new XElement(sync + "ApplicationData", new XElement(EasDataMapper.Tasks + "Subject", "Changed"), new XElement(EasDataMapper.Tasks + "DueDate", "2026-11-04T00:00:00.000Z"));
        var originalData = new XElement(sync + "ApplicationData", new XElement(EasDataMapper.Tasks + "Subject", "Original"), new XElement(EasDataMapper.Tasks + "DueDate", "2026-11-04T00:00:00.000Z"));
        var changed = EasDataMapper.ChangedFields(desiredData, originalData);
        Check(changed.Elements().Count() == 1 && changed.Element(EasDataMapper.Tasks + "Subject")?.Value == "Changed", "Unchanged provider fields are not overwritten");
        var restrictedAcknowledgements = 0;
        using (var restricted = new EasClient("https://eas.example.test", "person", "synthetic-password", accountId, new Handler(async request =>
        {
            if (request.Method == HttpMethod.Options)
            {
                var options = new HttpResponseMessage(HttpStatusCode.OK);
                options.Headers.Add("MS-ASProtocolVersions", "14.1");
                return options;
            }
            var doc = EasWbxml.Decode(await request.Content!.ReadAsByteArrayAsync());
            if (doc.Name.LocalName != "Provision") return new HttpResponseMessage((HttpStatusCode)449);
            XNamespace policy = "Provision:";
            if (doc.Descendants(policy + "Status").Any()) restrictedAcknowledgements++;
            return Response(new XElement(policy + "Provision", new XElement(policy + "Status", "1"),
                new XElement(policy + "Policies", new XElement(policy + "Policy", new XElement(policy + "Status", "1"),
                    new XElement(policy + "PolicyKey", "restricted-key"), new XElement(policy + "Data", new XElement(policy + "EASProvisionDoc",
                        new XElement(policy + "DevicePasswordEnabled", "1")))))));
        })))
        {
            try { await restricted.FolderSyncAsync("0", CancellationToken.None); throw new Exception("Restrictive policy accepted."); }
            catch (NotSupportedException) { Check(restrictedAcknowledgements == 0, "Unsupported device policy is never acknowledged"); }
        }
        using (var wiped = new EasClient("https://eas.example.test", "person", "synthetic-password", accountId, new Handler(request =>
        {
            if (request.Method == HttpMethod.Options)
            {
                var options = new HttpResponseMessage(HttpStatusCode.OK); options.Headers.Add("MS-ASProtocolVersions", "14.1"); return Task.FromResult(options);
            }
            return Task.FromResult(Response(new XElement(XName.Get("Provision", "Provision:"), new XElement(XName.Get("RemoteWipe", "Provision:")))));
        })))
        {
            try { await wiped.FolderSyncAsync("0", CancellationToken.None); throw new Exception("Wipe accepted."); }
            catch (NotSupportedException) { count++; }
        }
        using (var invalidKey = new EasClient("https://eas.example.test", "person", "synthetic-password", accountId, new Handler(request =>
        {
            if (request.Method == HttpMethod.Options)
            {
                var options = new HttpResponseMessage(HttpStatusCode.OK); options.Headers.Add("MS-ASProtocolVersions", "14.1"); return Task.FromResult(options);
            }
            return Task.FromResult(Response(new XElement(sync + "Sync", new XElement(sync + "Status", "3"))));
        })))
        {
            try { await invalidKey.SyncAsync("2", "stale-key", "Email", null, CancellationToken.None); throw new Exception("Invalid key accepted."); }
            catch (EasCommandException exception) { Check(exception.Status == "3", "Global invalid sync key is classified for recovery"); }
        }
        foreach (var protocolVersion in new[] { "12.0", "12.1" })
            using (var legacy = new EasClient("https://eas.example.test", "person", "synthetic-password", accountId, new Handler(async request =>
            {
                if (request.Method == HttpMethod.Options)
                {
                    var options = new HttpResponseMessage(HttpStatusCode.OK);
                    options.Headers.Add("MS-ASProtocolVersions", protocolVersion == "12.0" ? "2.5,12.0" : "12.0,12.1");
                    return options;
                }
                Check(request.Headers.GetValues("MS-ASProtocolVersion").Single() == protocolVersion, "12.x selects highest compatible version");
                var bytes = await request.Content!.ReadAsByteArrayAsync();
                if (request.RequestUri!.Query.Contains("Cmd=SendMail"))
                {
                    Check(request.RequestUri.Query.Contains("SaveInSent=T") && request.Content.Headers.ContentType!.MediaType == "message/rfc822" && opaque.SequenceEqual(bytes), "Legacy send uses raw MIME and save-in-sent query");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                }
                var doc = EasWbxml.Decode(bytes);
                if (doc.Name == folder + "FolderSync")
                    return Response(new XElement(folder + "FolderSync", new XElement(folder + "Status", "1"), new XElement(folder + "SyncKey", "legacy-folders")));
                XNamespace items = "ItemOperations:";
                if (doc.Name == items + "ItemOperations")
                {
                    Check(doc.Descendants(body + "BodyPreference").Single().Element(body + "Type")!.Value == "4", "12.x requests full MIME for fetch");
                    return Response(new XElement(items + "ItemOperations", new XElement(items + "Status", "1"),
                        new XElement(items + "Response", new XElement(items + "Fetch", new XElement(items + "Status", "1"),
                            new XElement(items + "Properties", new XElement(body + "Body", new XElement(body + "Type", "4"), new XElement(body + "Data", "MIME")))))));
                }
                var collection = doc.Element(sync + "Collections")!.Element(sync + "Collection")!;
                if (protocolVersion == "12.0")
                    Check(collection.Elements().First().Name == sync + "Class" &&
                        new[] { "Email", "Calendar", "Contacts", "Tasks" }.Contains(collection.Element(sync + "Class")!.Value), "12.0 sends collection Class before SyncKey");
                else
                    Check(collection.Element(sync + "Class") == null, "12.1 omits Collection Class");
                return Response(new XElement(sync + "Sync", new XElement(sync + "Collections", new XElement(sync + "Collection",
                    new XElement(sync + "Status", "1"), new XElement(sync + "SyncKey", "legacy-key")))));
            })))
            {
                Check((await legacy.FolderSyncAsync("0", CancellationToken.None)).Element(folder + "SyncKey")!.Value == "legacy-folders", "12.x folder handshake");
                foreach (var itemClass in new[] { "Email", "Calendar", "Contacts", "Tasks" })
                {
                    await legacy.SyncAsync("2", "0", itemClass, null, CancellationToken.None);
                    await legacy.SyncAsync("2", "legacy-key", itemClass, null, CancellationToken.None);
                }
                Check((await legacy.FetchAsync("2", "message-1", CancellationToken.None)).Element(body + "Body")!.Element(body + "Data")!.Value == "MIME", "12.x fetch response");
                await legacy.SendMailAsync(opaque, "legacy-send", CancellationToken.None);
            }
        Console.WriteLine($"EAS protocol smoke: {count} checks passed.");
    }

    static HttpResponseMessage Response(XElement xml) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(EasWbxml.Encode(xml)) };
    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request);
    }
}
