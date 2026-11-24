// Private networking for tenants that enforce publicNetworkAccess = Disabled on data services.
// Pattern follows Azure-Samples/functions-quickstart-dotnet-azd (VNet integration + private endpoints).
param location string
param tags object
param token string
param storageId string
param cosmosId string
param vaultId string

var endpoints = [
  { name: 'blob', target: storageId, group: 'blob', zone: 'privatelink.blob.${environment().suffixes.storage}' }
  { name: 'cosmos', target: cosmosId, group: 'Sql', zone: 'privatelink.documents.azure.com' }
  { name: 'vault', target: vaultId, group: 'vault', zone: 'privatelink.vaultcore.azure.net' }
]

resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: 'vnet-${token}'
  location: location
  tags: tags
  properties: {
    addressSpace: { addressPrefixes: [ '10.40.0.0/16' ] }
    subnets: [
      {
        name: 'private-endpoints'
        properties: { addressPrefix: '10.40.1.0/24', privateEndpointNetworkPolicies: 'Disabled' }
      }
      {
        name: 'app'
        properties: {
          addressPrefix: '10.40.2.0/24'
          delegations: [ { name: 'flex', properties: { serviceName: 'Microsoft.App/environments' } } ]
        }
      }
    ]
  }
}

resource zones 'Microsoft.Network/privateDnsZones@2024-06-01' = [for e in endpoints: {
  name: e.zone
  location: 'global'
  tags: tags
}]

resource zoneLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = [for (e, i) in endpoints: {
  parent: zones[i]
  name: 'link-${token}'
  location: 'global'
  properties: { registrationEnabled: false, virtualNetwork: { id: vnet.id } }
}]

resource pes 'Microsoft.Network/privateEndpoints@2024-05-01' = [for e in endpoints: {
  name: 'pe-${e.name}-${token}'
  location: location
  tags: tags
  properties: {
    subnet: { id: '${vnet.id}/subnets/private-endpoints' }
    privateLinkServiceConnections: [ { name: e.name, properties: { privateLinkServiceId: e.target, groupIds: [ e.group ] } } ]
  }
}]

resource zoneGroups 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = [for (e, i) in endpoints: {
  parent: pes[i]
  name: 'default'
  properties: { privateDnsZoneConfigs: [ { name: e.name, properties: { privateDnsZoneId: zones[i].id } } ] }
}]

output appSubnetId string = '${vnet.id}/subnets/app'
