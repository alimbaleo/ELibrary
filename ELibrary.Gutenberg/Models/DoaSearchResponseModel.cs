using Newtonsoft.Json;

namespace ELibrary.Infrastructure.Models
{
    internal class DoaBookResults
    {
        [JsonProperty("uuid")]
        public string Uuid;

        [JsonProperty("name")]
        public string Name;

        [JsonProperty("handle")]
        public string Handle;

        [JsonProperty("type")]
        public string Type;

        [JsonProperty("expand")]
        public List<string> Expand;

        [JsonProperty("lastModified")]
        public string LastModified;

        [JsonProperty("parentCollection")]
        public object ParentCollection;

        [JsonProperty("parentCollectionList")]
        public object ParentCollectionList;

        [JsonProperty("parentCommunityList")]
        public object ParentCommunityList;

        [JsonProperty("bitstreams")]
        public List<BitstreamData> Bitstreams;

        [JsonProperty("withdrawn")]
        public string Withdrawn;

        [JsonProperty("archived")]
        public string Archived;

        [JsonProperty("link")]
        public string Link;

        [JsonProperty("metadata")]
        public List<DoaMetaData> Metadata;
    }



    internal class DoaMetaData
    {
        [JsonProperty("key")]
        public string Key;

        [JsonProperty("value")]
        public object Value;

        [JsonProperty("language")]
        public string Language;

        [JsonProperty("schema")]
        public string Schema;

        [JsonProperty("element")]
        public string Element;

        [JsonProperty("qualifier")]
        public string Qualifier;

        [JsonProperty("code")]
        public string Code;
    }
    internal class BitstreamData
    {
        [JsonProperty("uuid")]
        public string Uuid { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("handle")]
        public object Handle { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("expand")]
        public List<string> Expand { get; set; }

        [JsonProperty("bundleName")]
        public string BundleName { get; set; }

        [JsonProperty("description")]
        public object Description { get; set; }

        [JsonProperty("format")]
        public string Format { get; set; }

        [JsonProperty("mimeType")]
        public string MimeType { get; set; }

        [JsonProperty("sizeBytes")]
        public int SizeBytes { get; set; }

        [JsonProperty("parentObject")]
        public object ParentObject { get; set; }

        [JsonProperty("retrieveLink")]
        public string RetrieveLink { get; set; }

        [JsonProperty("checkSum")]
        public CheckSum CheckSum { get; set; }

        [JsonProperty("sequenceId")]
        public int SequenceId { get; set; }

        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("policies")]
        public object Policies { get; set; }

        [JsonProperty("link")]
        public string Link { get; set; }

        [JsonProperty("metadata")]
        public List<DoaMetaData> Metadata { get; set; }
    }

}
