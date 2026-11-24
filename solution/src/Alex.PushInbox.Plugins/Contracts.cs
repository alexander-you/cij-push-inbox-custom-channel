using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Alex.PushInbox.Plugins
{
    // Subset of the CIJ outbound payload this plug-in reads. The full payload is forwarded unchanged.
    [DataContract]
    public class OutboundPayload
    {
        [DataMember] public Guid ChannelDefinitionId { get; set; }
        [DataMember] public string RequestId { get; set; }
        [DataMember] public string IdempotencyId { get; set; }
        [DataMember] public string From { get; set; }
        [DataMember] public string To { get; set; }
    }

    // The message parts, keyed by msdyn_name. Read separately so an unexpected shape can't break the send.
    [DataContract]
    public class OutboundMessage
    {
        [DataMember] public Dictionary<string, string> Message { get; set; }
    }

    [DataContract]
    public class OutboundResponse
    {
        [DataMember] public Guid ChannelDefinitionId { get; set; }
        [DataMember] public string RequestId { get; set; }
        [DataMember] public string MessageId { get; set; }
        [DataMember] public string Status { get; set; }
        [DataMember] public ResponseDetails Details { get; set; }
    }

    [DataContract]
    public class ResponseDetails
    {
        [DataMember] public string Reason { get; set; }
        [DataMember] public string Message { get; set; }
    }

    [DataContract]
    public class ChannelApiAccepted
    {
        [DataMember(Name = "messageId")] public string MessageId { get; set; }
    }

    [DataContract]
    public class DeliveryReport
    {
        [DataMember] public Guid ChannelDefinitionId { get; set; }
        [DataMember] public string RequestId { get; set; }
        [DataMember] public string MessageId { get; set; }
        [DataMember] public string From { get; set; }
        [DataMember] public string OrganizationId { get; set; }
        [DataMember] public string Status { get; set; }
        [DataMember(EmitDefaultValue = false)] public ResponseDetails Details { get; set; }
    }

    public static class Json
    {
        private static DataContractJsonSerializerSettings Settings => new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true };

        public static string Serialize<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T), Settings).WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public static T Deserialize<T>(string json)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (T)new DataContractJsonSerializer(typeof(T), Settings).ReadObject(stream);
            }
        }
    }
}
