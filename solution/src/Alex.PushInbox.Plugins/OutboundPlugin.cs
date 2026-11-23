using System;
using System.Linq;
using System.Net;
using System.Net.Http;
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
                Forward(baseUrl, raw, config, response, trace);
            }

            context.OutputParameters["response"] = Json.Serialize(response);
        }

        private static void Forward(string baseUrl, string raw, Entity config, OutboundResponse response, ITracingService trace)
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
