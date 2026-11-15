using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.MAPI;
using Wino.Core.MAPI.AddressBook;
using Wino.Core.MAPI.Rops;
using Wino.Core.MAPI.Transport;

namespace Wino.Core.Synchronizers.Mapi;

// The Global Address List read over the MAPI/HTTP address book endpoint (NSPI QueryRows), with EWS as the fallback
// when Autodiscover advertised no address book URL.
public sealed partial class MapiExchangeSynchronizer
{
    private const uint DirectoryPageSize = 500;

    // DT_MAILUSER covers people, rooms and equipment; DisplayTypeEx tells the last two apart.
    private const uint DisplayTypeMailUser = 0x00000000;
    private const uint DisplayTypeExRoom = 0x07;
    private const uint DisplayTypeExEquipment = 0x08;

    private static readonly uint[] DirectoryColumns =
    [
        PropertyTags.DisplayName, PropertyTags.SmtpAddress, NspiClient.GivenName, NspiClient.Surname,
        NspiClient.CompanyName, NspiClient.Title, NspiClient.DepartmentName, NspiClient.OfficeLocation,
        NspiClient.BusinessTelephoneNumber, NspiClient.HomeTelephoneNumber, NspiClient.MobileTelephoneNumber,
        NspiClient.DisplayType, NspiClient.DisplayTypeEx,
    ];

    protected override async Task<List<AccountContact>?> ReadDirectoryContactsAsync(CancellationToken cancellationToken)
    {
        var credential = await MapiConnectionProbe.ToMapiCredentialAsync(ExchangeAuthenticator, Account, cancellationToken).ConfigureAwait(false);
        var endpoint = await ResolveEndpointAsync(credential, cancellationToken).ConfigureAwait(false);

        if (endpoint.AddressBookUrl is not { } addressBookUrl)
        {
            Logger.Information("MAPI directory {Account}: Autodiscover advertised no address book endpoint; reading the directory over EWS.", Account.Address);
            return await base.ReadDirectoryContactsAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var nspi = new NspiClient(new MapiHttpTransport(addressBookUrl, credential, UserAgent, Diagnostics), Diagnostics);
        await nspi.BindAsync(cancellationToken).ConfigureAwait(false);
        var rows = await nspi.QueryAllRowsAsync(DirectoryColumns, DirectoryPageSize, DirectoryMaxEntries, cancellationToken).ConfigureAwait(false);

        var contacts = new List<AccountContact>();
        foreach (var row in rows)
        {
            if (Uint(row, NspiClient.DisplayType) is { } displayType && displayType != DisplayTypeMailUser)
                continue;

            if (Uint(row, NspiClient.DisplayTypeEx) is { } displayTypeEx && (displayTypeEx & 0xFF) is DisplayTypeExRoom or DisplayTypeExEquipment)
                continue;

            var contact = CreateDirectoryContact(
                Text(row, PropertyTags.SmtpAddress),
                Text(row, PropertyTags.DisplayName),
                Text(row, NspiClient.GivenName),
                Text(row, NspiClient.Surname),
                Text(row, NspiClient.CompanyName),
                Text(row, NspiClient.Title),
                Text(row, NspiClient.DepartmentName),
                Text(row, NspiClient.OfficeLocation),
                [
                    (Text(row, NspiClient.BusinessTelephoneNumber), ContactPhoneKind.Work),
                    (Text(row, NspiClient.MobileTelephoneNumber), ContactPhoneKind.Mobile),
                    (Text(row, NspiClient.HomeTelephoneNumber), ContactPhoneKind.Home),
                ]);

            if (contact != null)
                contacts.Add(contact);
        }

        Logger.Information("MAPI directory {Account}: {Rows} address book rows, {People} people.", Account.Address, rows.Count, contacts.Count);
        return contacts;
    }

    private static string? Text(PropertyValue[] row, uint tag)
    {
        foreach (var cell in row)
        {
            if (cell.Tag == tag)
                return cell.AsString;
        }

        return null;
    }

    private static uint? Uint(PropertyValue[] row, uint tag)
    {
        foreach (var cell in row)
        {
            if (cell.Tag == tag)
                return cell.AsUInt32;
        }

        return null;
    }
}
