using System;
using Microsoft.Xrm.Sdk;

namespace Alex.PushInbox.Plugins
{
    /// <summary>
    /// PreOperation Create on alex_pushchannelinstance: sets alex_defaultexpirydays to 30 when it is not provided,
    /// so records created outside the form (Web API, import, CIJ wizard) get the documented default.
    /// </summary>
    public class DefaultExpiryPlugin : IPlugin
    {
        internal const string Column = "alex_defaultexpirydays";
        internal const int DefaultDays = 30;

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            if (!(context.InputParameters["Target"] is Entity target)) return;

            if (!target.Contains(Column) || target[Column] == null)
            {
                target[Column] = DefaultDays;
            }
        }
    }
}
