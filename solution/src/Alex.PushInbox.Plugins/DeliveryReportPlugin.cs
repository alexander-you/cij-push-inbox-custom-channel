using System;
using Microsoft.Xrm.Sdk;

namespace Alex.PushInbox.Plugins
{
    /// <summary>
    /// Implements custom API alex_PushDeliveryReport. The Azure Dispatcher calls it as an application user
    /// with a delivery report (Delivered / NotDelivered); the plug-in relays it to CIJ via msdyn_D365ChannelsNotification.
    /// </summary>
    public class DeliveryReportPlugin : IPlugin
    {
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

            var report = Json.Deserialize<DeliveryReport>(raw);
            if (report.ChannelDefinitionId == Guid.Empty || string.IsNullOrEmpty(report.RequestId) || string.IsNullOrEmpty(report.From))
            {
                throw new InvalidPluginExecutionException("ChannelDefinitionId, RequestId and From are required.");
            }

            if (report.Status != "Delivered" && report.Status != "NotDelivered")
            {
                throw new InvalidPluginExecutionException("Status must be Delivered or NotDelivered.");
            }

            report.OrganizationId = context.OrganizationId.ToString();
            if (string.IsNullOrEmpty(report.MessageId)) report.MessageId = report.RequestId;

            var notification = Json.Serialize(report);
            trace.Trace("Notification payload: {0}", notification);

            var result = service.Execute(new OrganizationRequest("msdyn_D365ChannelsNotification")
            {
                Parameters = { { "notificationPayLoad", notification } },
            });

            context.OutputParameters["response"] = result.Results.Contains("responseMessage") ? result.Results["responseMessage"] as string : string.Empty;
        }
    }
}
