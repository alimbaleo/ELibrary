using Newtonsoft.Json;

namespace ELibrary.Infrastructure.Models;

public class DoaBookResponseModel
{
    [JsonProperty("uuid")] public string Uuid { get; set; }

    [JsonProperty("name")] public string Name { get; set; }

    [JsonProperty("handle")] public string Handle { get; set; }

    [JsonProperty("type")] public string Type { get; set; }

    [JsonProperty("expand")] public List<string> Expand { get; set; }

    [JsonProperty("lastModified")] public string LastModified { get; set; }

    [JsonProperty("parentCollection")] public ParentCollection ParentCollection { get; set; }

    [JsonProperty("parentCollectionList")] public List<ParentCollection> ParentCollectionList { get; set; }

    [JsonProperty("parentCommunityList")] public List<ParentCommunity> ParentCommunityList { get; set; }

    [JsonProperty("bitstreams")] public List<Bitstream> Bitstreams { get; set; }

    [JsonProperty("withdrawn")] public string Withdrawn { get; set; }

    [JsonProperty("archived")] public string Archived { get; set; }

    [JsonProperty("link")] public string Link { get; set; }

    [JsonProperty("metadata")] public List<MetadataEntry> Metadata { get; set; }
}

public class ParentCollection
{
    [JsonProperty("uuid")] public string Uuid { get; set; }

    [JsonProperty("name")] public string Name { get; set; }

    [JsonProperty("handle")] public string Handle { get; set; }

    [JsonProperty("type")] public string Type { get; set; }

    [JsonProperty("expand")] public List<string> Expand { get; set; }

    [JsonProperty("logo")] public object Logo { get; set; }

    [JsonProperty("parentCommunity")] public object ParentCommunity { get; set; }

    [JsonProperty("parentCommunityList")] public List<object> ParentCommunityList { get; set; }

    [JsonProperty("items")] public List<object> Items { get; set; }

    [JsonProperty("license")] public object License { get; set; }

    [JsonProperty("copyrightText")] public string CopyrightText { get; set; }

    [JsonProperty("introductoryText")] public string IntroductoryText { get; set; }

    [JsonProperty("shortDescription")] public string ShortDescription { get; set; }

    [JsonProperty("sidebarText")] public string SidebarText { get; set; }

    [JsonProperty("numberItems")] public int NumberItems { get; set; }

    [JsonProperty("link")] public string Link { get; set; }
}

public class ParentCommunity
{
    [JsonProperty("uuid")] public string Uuid { get; set; }

    [JsonProperty("name")] public string Name { get; set; }

    [JsonProperty("handle")] public string Handle { get; set; }

    [JsonProperty("type")] public string Type { get; set; }

    [JsonProperty("expand")] public List<string> Expand { get; set; }

    [JsonProperty("logo")] public object Logo { get; set; }

    [JsonProperty("parentCommunity")] public object ParentCommunityValue { get; set; }

    [JsonProperty("copyrightText")] public string CopyrightText { get; set; }

    [JsonProperty("introductoryText")] public string IntroductoryText { get; set; }

    [JsonProperty("shortDescription")] public string ShortDescription { get; set; }

    [JsonProperty("sidebarText")] public string SidebarText { get; set; }

    [JsonProperty("countItems")] public int CountItems { get; set; }

    [JsonProperty("collections")] public List<object> Collections { get; set; }

    [JsonProperty("link")] public string Link { get; set; }

    [JsonProperty("subcommunities")] public List<object> Subcommunities { get; set; }
}

public class Bitstream
{
    [JsonProperty("uuid")] public string Uuid { get; set; }

    [JsonProperty("name")] public string Name { get; set; }

    [JsonProperty("handle")] public string Handle { get; set; }

    [JsonProperty("type")] public string Type { get; set; }

    [JsonProperty("expand")] public List<string> Expand { get; set; }

    [JsonProperty("bundleName")] public string BundleName { get; set; }

    [JsonProperty("description")] public string Description { get; set; }

    [JsonProperty("format")] public string Format { get; set; }

    [JsonProperty("mimeType")] public string MimeType { get; set; }

    [JsonProperty("sizeBytes")] public long SizeBytes { get; set; }

    [JsonProperty("parentObject")] public ParentObject ParentObject { get; set; }

    [JsonProperty("retrieveLink")] public string RetrieveLink { get; set; }

    [JsonProperty("checkSum")] public CheckSum CheckSum { get; set; }

    [JsonProperty("sequenceId")] public int SequenceId { get; set; }

    [JsonProperty("code")] public string Code { get; set; }

    [JsonProperty("policies")] public List<Policy> Policies { get; set; }

    [JsonProperty("link")] public string Link { get; set; }

    [JsonProperty("metadata")] public List<MetadataEntry> Metadata { get; set; }
}

public class ParentObject
{
    [JsonProperty("uuid")] public string Uuid { get; set; }

    [JsonProperty("name")] public string Name { get; set; }

    [JsonProperty("handle")] public string Handle { get; set; }

    [JsonProperty("type")] public string Type { get; set; }

    [JsonProperty("expand")] public List<string> Expand { get; set; }

    [JsonProperty("link")] public string Link { get; set; }
}

public class CheckSum
{
    [JsonProperty("value")] public string Value { get; set; }

    [JsonProperty("checkSumAlgorithm")] public string CheckSumAlgorithm { get; set; }
}

public class Policy
{
    [JsonProperty("id")] public long Id { get; set; }

    [JsonProperty("action")] public string Action { get; set; }

    [JsonProperty("epersonId")] public string EpersonId { get; set; }

    [JsonProperty("groupId")] public string GroupId { get; set; }

    [JsonProperty("resourceId")] public string ResourceId { get; set; }

    [JsonProperty("resourceType")] public string ResourceType { get; set; }

    [JsonProperty("rpDescription")] public string RpDescription { get; set; }

    [JsonProperty("rpName")] public string RpName { get; set; }

    [JsonProperty("rpType")] public string RpType { get; set; }

    [JsonProperty("startDate")] public string StartDate { get; set; }

    [JsonProperty("endDate")] public string EndDate { get; set; }
}

public class MetadataEntry
{
    [JsonProperty("key")] public string Key { get; set; }

    [JsonProperty("value")] public string Value { get; set; }

    [JsonProperty("language")] public string Language { get; set; }

    [JsonProperty("schema")] public string Schema { get; set; }

    [JsonProperty("element")] public string Element { get; set; }

    [JsonProperty("qualifier")] public string Qualifier { get; set; }
}
