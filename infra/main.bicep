targetScope = 'subscription'

@minLength(1)
@maxLength(40)
@description('azd environment name; used for the resource group and resource names.')
param environmentName string

@description('Azure region for all resources.')
param location string

@description('Resource group name. Defaults to rg-<environmentName>.')
param resourceGroupName string = ''

@description('Dataverse environment URL (https://<org>.crm<n>.dynamics.com). Used for delivery reports.')
param dataverseUrl string = ''

@description('VAPID public key (base64url) for Web Push.')
param vapidPublicKey string

@secure()
@description('VAPID private key (base64url) for Web Push. Stored only on the notification hub.')
param vapidPrivateKey string

@description('VAPID subject, a mailto: or https: URL identifying the sender.')
param vapidSubject string

@description('Cosmos DB region. Empty = same as location.')
param cosmosLocation string = ''

@description('Monthly budget for the resource group, in the billing currency.')
param budgetAmount int = 50

@description('Comma-separated e-mail addresses notified by the budget alert. Empty = no budget.')
param budgetContactEmails string = ''

@description('First day of the budget period (yyyy-MM-01).')
param budgetStartDate string = utcNow('yyyy-MM-01')

var tags = { 'azd-env-name': environmentName }
var rgName = empty(resourceGroupName) ? 'rg-${environmentName}' : resourceGroupName

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: rgName
  location: location
  tags: tags
}

module resources 'resources.bicep' = {
  name: 'resources'
  scope: rg
  params: {
    location: location
    cosmosLocation: empty(cosmosLocation) ? location : cosmosLocation
    tags: tags
    environmentName: environmentName
    dataverseUrl: dataverseUrl
    vapidPublicKey: vapidPublicKey
    vapidPrivateKey: vapidPrivateKey
    vapidSubject: vapidSubject
    budgetAmount: budgetAmount
    budgetContactEmails: empty(budgetContactEmails) ? [] : split(replace(budgetContactEmails, ' ', ''), ',')
    budgetStartDate: budgetStartDate
  }
}

output AZURE_RESOURCE_GROUP string = rg.name
output AZURE_LOCATION string = location
output API_BASE_URL string = resources.outputs.apiBaseUrl
output FUNCTION_APP_NAME string = resources.outputs.functionAppName
output FUNCTION_PRINCIPAL_ID string = resources.outputs.functionPrincipalId
output WEB_URL string = resources.outputs.webUrl
output NOTIFICATION_HUB_NAMESPACE string = resources.outputs.notificationHubNamespace
output NOTIFICATION_HUB_NAME string = resources.outputs.notificationHubName
