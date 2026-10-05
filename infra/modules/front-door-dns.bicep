// Points a custom domain at its Azure Front Door endpoint in an existing Azure DNS zone and
// publishes the domain-validation TXT record Front Door needs to issue the managed certificate.

@description('Name of the existing Azure DNS zone, for example contoso.com.')
param dnsZoneName string

@description('Record name relative to the zone, for example devbrain.')
param recordName string

@description('Front Door endpoint host name the CNAME points at.')
param endpointHostName string

@description('Front Door domain-validation token. Empty once Front Door no longer reports one.')
param validationToken string

resource dnsZone 'Microsoft.Network/dnsZones@2018-05-01' existing = {
  name: dnsZoneName
}

resource cname 'Microsoft.Network/dnsZones/CNAME@2018-05-01' = {
  parent: dnsZone
  name: recordName
  properties: {
    TTL: 3600
    CNAMERecord: {
      cname: endpointHostName
    }
  }
}

resource validation 'Microsoft.Network/dnsZones/TXT@2018-05-01' = if (!empty(validationToken)) {
  parent: dnsZone
  name: '_dnsauth.${recordName}'
  properties: {
    TTL: 3600
    TXTRecords: [
      {
        value: [validationToken]
      }
    ]
  }
}
