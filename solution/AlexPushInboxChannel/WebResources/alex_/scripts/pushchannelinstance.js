// Form script for the Push sender form (alex_pushchannelinstance).
var Alex = window.Alex || {};
Alex.PushChannelInstance = {
    defaultExpiryDays: 30,
    onLoad: function (executionContext) {
        var formContext = executionContext.getFormContext();
        var CREATE = 1;
        if (formContext.ui.getFormType() !== CREATE) return;
        var attr = formContext.getAttribute("alex_defaultexpirydays");
        if (attr && (attr.getValue() === null || attr.getValue() === 0)) {
            attr.setValue(Alex.PushChannelInstance.defaultExpiryDays);
        }
    }
};
window.Alex = Alex;
