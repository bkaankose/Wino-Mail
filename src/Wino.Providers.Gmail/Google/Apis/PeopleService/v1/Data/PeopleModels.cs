using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wino.Providers.Gmail.Google.Apis.PeopleService.v1.Data;

public sealed class EmailAddress
{
    public FieldMetadata Metadata { get; set; }

    public string Value { get; set; }
    public string Type { get; set; }
}

public sealed class FieldMetadata
{
    public bool? Primary { get; set; }
    public bool? SourcePrimary { get; set; }
    public Source Source { get; set; }
}

public sealed class Source { public string Type { get; set; } public string Id { get; set; } }

public sealed class Name
{
    public string DisplayName { get; set; }
    public string UnstructuredName { get; set; }
    public string HonorificPrefix { get; set; }
    public string GivenName { get; set; }
    public string MiddleName { get; set; }
    public string FamilyName { get; set; }
    public string HonorificSuffix { get; set; }

    public FieldMetadata Metadata { get; set; }
}

public sealed class Person
{
    public string ResourceName { get; set; }
    public string Etag { get; set; }
    public IList<EmailAddress> EmailAddresses { get; set; }

    public IList<Name> Names { get; set; }

    public IList<Photo> Photos { get; set; }
    public IList<PhoneNumber> PhoneNumbers { get; set; }
    public IList<Address> Addresses { get; set; }
    public IList<Organization> Organizations { get; set; }
    public IList<Birthday> Birthdays { get; set; }
    public IList<Biography> Biographies { get; set; }
    public IList<Url> Urls { get; set; }
    public IList<Nickname> Nicknames { get; set; }
    public IList<FileAs> FileAses { get; set; }
    public IList<ImClient> ImClients { get; set; }
    public IList<Relation> Relations { get; set; }
    public IList<Membership> Memberships { get; set; }
    public PersonMetadata Metadata { get; set; }
}

public sealed class Membership { public ContactGroupMembership ContactGroupMembership { get; set; } }
public sealed class ContactGroupMembership { public string ContactGroupResourceName { get; set; } }

public sealed class ContactGroup
{
    public string ResourceName { get; set; }
    public string Etag { get; set; }
    public string Name { get; set; }
    public string GroupType { get; set; }
    public ContactGroupMetadata Metadata { get; set; }
}

public sealed class ContactGroupMetadata { public bool? Deleted { get; set; } }

public sealed class ListContactGroupsResponse
{
    public IList<ContactGroup> ContactGroups { get; set; }
    public string NextPageToken { get; set; }
}

public sealed class CreateContactGroupRequest { public ContactGroup ContactGroup { get; set; } }

public sealed class UpdateContactGroupRequest
{
    public ContactGroup ContactGroup { get; set; }
    public string UpdateGroupFields { get; set; }
}

public sealed class ModifyContactGroupMembersRequest
{
    public IList<string> ResourceNamesToAdd { get; set; }
    public IList<string> ResourceNamesToRemove { get; set; }
}

public sealed class ModifyContactGroupMembersResponse
{
    public IList<string> NotFoundResourceNames { get; set; }
    public IList<string> CanNotRemoveLastContactGroupResourceNames { get; set; }
}

public sealed class PersonMetadata { public bool? Deleted { get; set; } }
public sealed class PhoneNumber { public string Value { get; set; } public string Type { get; set; } public FieldMetadata Metadata { get; set; } }
public sealed class Address { public string Type { get; set; } public string PoBox { get; set; } public string StreetAddress { get; set; } public string City { get; set; } public string Region { get; set; } public string PostalCode { get; set; } public string Country { get; set; } public FieldMetadata Metadata { get; set; } }
public sealed class Organization { public string Name { get; set; } public string Department { get; set; } public string Title { get; set; } public string Location { get; set; } public string JobDescription { get; set; } public FieldMetadata Metadata { get; set; } }
public sealed class Birthday { public Date Date { get; set; } public FieldMetadata Metadata { get; set; } }
public sealed class Date { public int? Year { get; set; } public int? Month { get; set; } public int? Day { get; set; } }
public sealed class Biography { public string Value { get; set; } public string ContentType { get; set; } public FieldMetadata Metadata { get; set; } }
public sealed class Url { public string Value { get; set; } public FieldMetadata Metadata { get; set; } }
public sealed class Nickname { public string Value { get; set; } public FieldMetadata Metadata { get; set; } }
public sealed class FileAs { public string Value { get; set; } public FieldMetadata Metadata { get; set; } }
public sealed class ImClient { public string Username { get; set; } public string Protocol { get; set; } public FieldMetadata Metadata { get; set; } }
public sealed class Relation { public string Person { get; set; } public string Type { get; set; } public FieldMetadata Metadata { get; set; } }
public sealed class ListConnectionsResponse
{
    public IList<Person> Connections { get; set; }
    public string NextPageToken { get; set; }
    public string NextSyncToken { get; set; }
}
public sealed class UpdateContactPhotoRequest { public string PhotoBytes { get; set; } }

public sealed class Photo
{
    public FieldMetadata Metadata { get; set; }

    public string Url { get; set; }
}
