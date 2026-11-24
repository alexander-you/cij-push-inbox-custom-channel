using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.ServiceModel;
using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Alex.PushInbox.Plugins
{
    /// <summary>
    /// Implements custom API alex_PushOutbound. CIJ calls it for every push message.
    /// Forwards the payload to the Azure Channel API at {alex_ChannelApiBaseUrl}/messages.
    /// While the environment variable is empty, returns NotSent with reason ChannelApiNotConfigured.
    /// </summary>
    public class OutboundPlugin : IPlugin
    {
        internal const string BaseUrlVariable = "alex_ChannelApiBaseUrl";
        internal const string ImagePart = "imageId";
        internal const string ImageUrlHeader = "x-push-image-url";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
        private static readonly HttpClient Http = new HttpClient { Timeout = Timeout };

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var trace = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var service = ((IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory))).CreateOrganizationService(null);

            var raw = context.InputParameters["payload"] as string;
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new InvalidPluginExecutionException("The payload parameter is empty.");
            }

            TraceRequest(context, raw, trace);
            var payload = Json.Deserialize<OutboundPayload>(raw);
            trace.Trace("RequestId {0}, ChannelDefinitionId {1}", payload.RequestId, payload.ChannelDefinitionId);

            var response = new OutboundResponse
            {
                ChannelDefinitionId = payload.ChannelDefinitionId,
                RequestId = payload.RequestId,
                MessageId = payload.RequestId,
            };

            var baseUrl = GetEnvironmentVariable(service, BaseUrlVariable);
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                response.Status = "NotSent";
                response.Details = new ResponseDetails
                {
                    Reason = "ChannelApiNotConfigured",
                    Message = "Set the environment variable 'Push channel service address' (alex_ChannelApiBaseUrl) to the Azure Channel API address.",
                };
            }
            else
            {
                var config = GetSenderConfig(service, payload);
                var imageUrl = ResolveImage(service, ReadImagePart(raw, trace), trace);
                Forward(baseUrl, raw, config, imageUrl, response, trace);
            }

            var serialized = Json.Serialize(response);
            trace.Trace("Response: {0}", serialized);
            context.OutputParameters["response"] = serialized;
        }

        // Traces only non-personal metadata. The CIJ payload (recipient and message content) is never written to the trace log.
        private static void TraceRequest(IPluginExecutionContext context, string raw, ITracingService trace)
        {
            trace.Trace("Message {0}, Depth {1}, CorrelationId {2}, payload length {3}", context.MessageName, context.Depth, context.CorrelationId, raw.Length);
        }

        private static void Forward(string baseUrl, string raw, Entity config, string imageUrl, OutboundResponse response, ITracingService trace)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/messages")
            {
                Content = new StringContent(raw, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("x-push-app-key", config?.GetAttributeValue<string>("alex_appkey") ?? string.Empty);
            var expiry = config?.GetAttributeValue<int?>("alex_defaultexpirydays");
            if (expiry.HasValue)
            {
                request.Headers.Add("x-push-default-expiry-days", expiry.Value.ToString());
            }

            if (imageUrl != null)
            {
                request.Headers.Add(ImageUrlHeader, imageUrl);
            }

            var key = config?.GetAttributeValue<string>("alex_channelapikey");
            if (!string.IsNullOrEmpty(key))
            {
                request.Headers.Add("x-functions-key", key);
            }

            HttpResponseMessage result;
            try
            {
                result = Http.SendAsync(request).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // Retriable: let CIJ retry. Azure de-duplicates on IdempotencyId.
                throw new InvalidPluginExecutionException("Channel API call failed: " + ex.Message, ex);
            }

            var body = result.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            trace.Trace("Channel API returned {0}", (int)result.StatusCode);

            if (result.IsSuccessStatusCode)
            {
                response.Status = "Sent";
                try
                {
                    var accepted = Json.Deserialize<ChannelApiAccepted>(body);
                    if (!string.IsNullOrEmpty(accepted?.MessageId)) response.MessageId = accepted.MessageId;
                }
                catch (Exception)
                {
                    // Keep RequestId as MessageId when the body isn't JSON.
                }
            }
            else if ((int)result.StatusCode >= 400 && (int)result.StatusCode < 500 && result.StatusCode != (HttpStatusCode)429)
            {
                response.Status = "NotSent";
                response.Details = new ResponseDetails { Reason = "ChannelApi" + (int)result.StatusCode, Message = Truncate(body, 500) };
            }
            else
            {
                throw new InvalidPluginExecutionException("Channel API returned " + (int)result.StatusCode + ". CIJ will retry.");
            }
        }

        private static string ReadImagePart(string raw, ITracingService trace)
        {
            try
            {
                var parts = Json.Deserialize<OutboundMessage>(raw).Message;
                string value = null;
                parts?.TryGetValue(ImagePart, out value);
                return value;
            }
            catch (Exception ex)
            {
                trace.Trace("Image part not read: {0}", ex.GetType().Name);
                return null;
            }
        }

        /// <summary>
        /// An image part (type image) holds the msdyncrm_file ID of an asset-library image.
        /// Returns the asset's public CDN URL (msdyncrm_blobcdnuri). An https URL passes through.
        /// Anything else returns null: the message is sent without an image.
        /// </summary>
        internal static string ResolveImage(IOrganizationService service, string value, ITracingService trace)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = value.Trim();

            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                if (uri.Scheme == Uri.UriSchemeHttps) return uri.AbsoluteUri;
                trace.Trace("Image not sent: the value is a URL but not https.");
                return null;
            }

            if (!Guid.TryParse(value, out var fileId))
            {
                trace.Trace("Image not sent: the value is neither an asset ID nor an https URL.");
                return null;
            }

            Entity file;
            try
            {
                file = service.Retrieve("msdyncrm_file", fileId, new ColumnSet("msdyncrm_blobcdnuri", "msdyncrm_contenttype"));
            }
            catch (FaultException<OrganizationServiceFault> ex)
            {
                trace.Trace("Image not sent: asset {0} could not be read ({1}).", fileId, ex.Detail?.ErrorCode);
                return null;
            }

            var cdn = file.GetAttributeValue<string>("msdyncrm_blobcdnuri");
            var contentType = file.GetAttributeValue<string>("msdyncrm_contenttype") ?? string.Empty;
            if (!Uri.TryCreate(cdn, UriKind.Absolute, out var cdnUri) || cdnUri.Scheme != Uri.UriSchemeHttps)
            {
                trace.Trace("Image not sent: asset {0} has no public https URL.", fileId);
                return null;
            }

            if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                trace.Trace("Image not sent: asset {0} is not an image.", fileId);
                return null;
            }

            return cdnUri.AbsoluteUri;
        }

        private static Entity GetSenderConfig(IOrganizationService service, OutboundPayload payload)
        {
            var query = new QueryExpression("msdyn_channelinstance") { ColumnSet = new ColumnSet("msdyn_extendedentityid"), TopCount = 1 };
            query.Criteria.AddCondition("msdyn_channeldefinitionid", ConditionOperator.Equal, payload.ChannelDefinitionId);
            query.Criteria.AddCondition("msdyn_contactpoint", ConditionOperator.Equal, payload.From);
            var reference = service.RetrieveMultiple(query).Entities.FirstOrDefault()?.GetAttributeValue<EntityReference>("msdyn_extendedentityid");
            return reference == null
                ? null
                : service.Retrieve(reference.LogicalName, reference.Id, new ColumnSet("alex_appkey", "alex_defaultexpirydays", "alex_channelapikey"));
        }

        internal static string GetEnvironmentVariable(IOrganizationService service, string schemaName)
        {
            var query = new QueryExpression("environmentvariabledefinition") { ColumnSet = new ColumnSet("defaultvalue"), TopCount = 1 };
            query.Criteria.AddCondition("schemaname", ConditionOperator.Equal, schemaName);
            var value = query.AddLink("environmentvariablevalue", "environmentvariabledefinitionid", "environmentvariabledefinitionid", JoinOperator.LeftOuter);
            value.EntityAlias = "v";
            value.Columns = new ColumnSet("value");
            var row = service.RetrieveMultiple(query).Entities.FirstOrDefault();
            if (row == null) return null;
            var current = row.GetAttributeValue<AliasedValue>("v.value")?.Value as string;
            return string.IsNullOrWhiteSpace(current) ? row.GetAttributeValue<string>("defaultvalue") : current;
        }

        private static string Truncate(string text, int max) => text == null || text.Length <= max ? text : text.Substring(0, max);
    }
}
