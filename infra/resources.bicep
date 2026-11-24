param location string
@description('Region for Cosmos DB; can differ from location when a region has no Cosmos DB capacity.')
param cosmosLocation string = location
param tags object
param environmentName string
param dataverseUrl string
param vapidPublicKey string
@secure()
param vapidPrivateKey string
param vapidSubject string
param budgetAmount int
param budgetContactEmails array
param budgetStartDate string

var token = toLower(uniqueString(subscription().id, resourceGroup().id, environmentName))
var databaseName = 'pushinbox'
var queueName = 'dispatch'
var hubName = 'pushinbox'

// Built-in role definition IDs (https://learn.microsoft.com/azure/role-based-access-control/built-in-roles)
var roles = {
  storageBlobDataOwner: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  storageQueueDataContributor: '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
  storageTableDataContributor: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
  serviceBusDataSender: '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
  serviceBusDataReceiver: '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${token}'
  location: location
  tags: tags
  properties: { sku: { name: 'PerGB2018' }, retentionInDays: 30 }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${token}'
  location: location
  tags: tags
  kind: 'web'
  properties: { Application_Type: 'web', WorkspaceResourceId: logs.id }
}

// Function host storage and Flex Consumption deployment container; identity-based access only.
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'st${token}'
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    // Flex Consumption package staging reaches storage outside the VNet; a network security perimeter
    // with a subscription-scoped inbound rule allows it while public access stays closed.
    publicNetworkAccess: 'SecuredByPerimeter'
    networkAcls: { defaultAction: 'Deny', bypass: 'None' }
    minimumTlsVersion: 'TLS1_2'
  }
  resource blob 'blobServices' = {
    name: 'default'
    resource deployments 'containers' = { name: 'deployments' }
  }
}

resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: 'sb-${token}'
  location: location
  tags: tags
  sku: { name: 'Basic', tier: 'Basic' }
  properties: { disableLocalAuth: true, minimumTlsVersion: '1.2' }
  resource queue 'queues' = {
    name: queueName
    properties: { maxDeliveryCount: 5, lockDuration: 'PT1M', deadLetteringOnMessageExpiration: true, defaultMessageTimeToLive: 'P1D' }
  }
}

// Cosmos DB for NoSQL, serverless. Inbox expiry uses per-item TTL (container defaultTtl = -1).
resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: 'cosmos-${token}'
  location: cosmosLocation
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    publicNetworkAccess: 'Disabled'
    databaseAccountOfferType: 'Standard'
    capabilities: [ { name: 'EnableServerless' } ]
    locations: [ { locationName: cosmosLocation, failoverPriority: 0 } ]
    consistencyPolicy: { defaultConsistencyLevel: 'Session' }
    disableLocalAuth: true
  }
  resource db 'sqlDatabases' = {
    name: databaseName
    properties: { resource: { id: databaseName } }
    resource inbox 'containers' = {
      name: 'inboxItems'
      properties: {
        resource: { id: 'inboxItems', partitionKey: { paths: [ '/recipientKey' ], kind: 'Hash' }, defaultTtl: -1 }
      }
    }
    resource dispatches 'containers' = {
      name: 'dispatches'
      properties: {
        resource: { id: 'dispatches', partitionKey: { paths: [ '/idempotencyId' ], kind: 'Hash' }, defaultTtl: 2592000 }
      }
    }
  }
}

// Notification Hubs (Free tier) with Web Push (VAPID) credentials.
resource anhNamespace 'Microsoft.NotificationHubs/namespaces@2023-10-01-preview' = {
  name: 'ntfns-${token}'
  location: location
  tags: tags
  sku: { name: 'Free' }
  properties: { namespaceType: 'NotificationHub' }
}

resource hub 'Microsoft.NotificationHubs/namespaces/notificationHubs@2023-10-01-preview' = {
  parent: anhNamespace
  name: hubName
  location: location
  tags: tags
  properties: {
    browserCredential: {
      properties: { subject: vapidSubject, vapidPublicKey: vapidPublicKey, vapidPrivateKey: vapidPrivateKey }
    }
  }
}

// Least privilege for the backend: Listen (installations) + Send.
resource hubRule 'Microsoft.NotificationHubs/namespaces/notificationHubs/authorizationRules@2023-10-01-preview' = {
  parent: hub
  name: 'backend-listen-send'
  properties: { rights: [ 'Listen', 'Send' ] }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'kv-${token}'
  location: location
  tags: tags
  properties: {
    publicNetworkAccess: 'Disabled'
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
  }
  resource anhSecret 'secrets' = {
    name: 'NotificationHubConnection'
    properties: { value: hubRule.listKeys().primaryConnectionString }
  }
}

module network 'network.bicep' = {
  name: 'network'
  params: {
    location: location
    tags: tags
    token: token
    storageId: storage.id
    cosmosId: cosmos.id
    vaultId: vault.id
  }
}

resource nsp 'Microsoft.Network/networkSecurityPerimeters@2024-07-01' = {
  name: 'nsp-${token}'
  location: location
  tags: tags
  resource profile 'profiles' = {
    name: 'default'
    location: location
    resource allowSubscription 'accessRules' = {
      name: 'allow-subscription'
      location: location
      properties: { direction: 'Inbound', subscriptions: [ { id: subscription().id } ] }
    }
  }
  resource storageAssociation 'resourceAssociations' = {
    name: 'storage'
    location: location
    properties: {
      privateLinkResource: { id: storage.id }
      profile: { id: nsp::profile.id }
      accessMode: 'Enforced'
    }
  }
}

resource web 'Microsoft.Web/staticSites@2023-12-01' = {
  name: 'swa-${token}'
  location: location
  tags: union(tags, { 'azd-service-name': 'web' })
  sku: { name: 'Free', tier: 'Free' }
  properties: {}
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'plan-${token}'
  location: location
  tags: tags
  kind: 'functionapp'
  sku: { name: 'FC1', tier: 'FlexConsumption' }
  properties: { reserved: true }
}

resource func 'Microsoft.Web/sites@2024-04-01' = {
  name: 'func-${token}'
  location: location
  tags: union(tags, { 'azd-service-name': 'api' })
  kind: 'functionapp,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    virtualNetworkSubnetId: network.outputs.appSubnetId
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}deployments'
          authentication: { type: 'SystemAssignedIdentity' }
        }
      }
      scaleAndConcurrency: { maximumInstanceCount: 40, instanceMemoryMB: 2048 }
      runtime: { name: 'dotnet-isolated', version: '10.0' }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      cors: { allowedOrigins: [ 'https://${web.properties.defaultHostname}' ] }
      appSettings: [
        { name: 'AzureWebJobsStorage__accountName', value: storage.name }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'ServiceBus__fullyQualifiedNamespace', value: '${serviceBus.name}.servicebus.windows.net' }
        { name: 'DISPATCH_QUEUE', value: queueName }
        { name: 'COSMOS_ENDPOINT', value: cosmos.properties.documentEndpoint }
        { name: 'COSMOS_DATABASE', value: databaseName }
        { name: 'NOTIFICATIONHUB_NAME', value: hubName }
        { name: 'NOTIFICATIONHUB_CONNECTION', value: '@Microsoft.KeyVault(VaultName=${vault.name};SecretName=NotificationHubConnection)' }
        { name: 'VAPID_PUBLIC_KEY', value: vapidPublicKey }
        { name: 'DATAVERSE_URL', value: dataverseUrl }
      ]
    }
  }
}

var principalId = func.identity.principalId

resource storageRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for r in [ roles.storageBlobDataOwner, roles.storageQueueDataContributor, roles.storageTableDataContributor ]: {
  name: guid(storage.id, func.id, r)
  scope: storage
  properties: { principalId: principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', r) }
}]

resource busRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for r in [ roles.serviceBusDataSender, roles.serviceBusDataReceiver ]: {
  name: guid(serviceBus.id, func.id, r)
  scope: serviceBus
  properties: { principalId: principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', r) }
}]

resource vaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, func.id, roles.keyVaultSecretsUser)
  scope: vault
  properties: { principalId: principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser) }
}

// Cosmos DB Built-in Data Contributor (data plane RBAC).
resource cosmosRole 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = {
  parent: cosmos
  name: guid(cosmos.id, func.id, 'data-contributor')
  properties: {
    principalId: principalId
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    scope: cosmos.id
  }
}

resource budget 'Microsoft.Consumption/budgets@2023-11-01' = if (!empty(budgetContactEmails)) {
  name: 'budget-${environmentName}'
  properties: {
    category: 'Cost'
    amount: budgetAmount
    timeGrain: 'Monthly'
    timePeriod: { startDate: budgetStartDate }
    notifications: {
      actual80: { enabled: true, operator: 'GreaterThanOrEqualTo', threshold: 80, thresholdType: 'Actual', contactEmails: budgetContactEmails }
      forecast100: { enabled: true, operator: 'GreaterThanOrEqualTo', threshold: 100, thresholdType: 'Forecasted', contactEmails: budgetContactEmails }
    }
  }
}

output apiBaseUrl string = 'https://${func.properties.defaultHostName}/api'
output functionAppName string = func.name
output functionPrincipalId string = principalId
output webUrl string = 'https://${web.properties.defaultHostname}'
output notificationHubNamespace string = anhNamespace.name
output notificationHubName string = hub.name
