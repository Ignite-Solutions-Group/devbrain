// Publishes DevBrain through an existing Azure Front Door Standard/Premium profile.
//
// Everything here is a new child resource of the profile (endpoint, origin group, origin, custom
// domain, route, security policy) plus a dedicated WAF policy, so other sites on the same profile
// keep their own routes and WAF rules untouched.

@description('Name of the existing Azure Front Door Standard/Premium profile.')
param profileName string

@description('SKU of the Front Door profile. The WAF policy SKU must match it.')
@allowed([
  'Standard_AzureFrontDoor'
  'Premium_AzureFrontDoor'
])
param skuName string = 'Standard_AzureFrontDoor'

@description('Suffix that keeps DevBrain resource names unique within the profile.')
param resourceToken string

@description('Public host name clients use, for example devbrain.contoso.com.')
param customDomainName string

@description('Container Apps host name that Front Door forwards to.')
param originHostName string

@description('Resource ID of the Azure DNS zone hosting customDomainName, or empty when DNS is managed elsewhere.')
param dnsZoneId string = ''

@description('Two-letter country codes allowed through the WAF. Empty allows every country.')
param allowedCountryCodes array = []

@description('Requests per minute per client IP allowed to the OAuth endpoints.')
@minValue(10)
param oauthRateLimitPerMinute int = 30

@description('Requests per five minutes per client IP allowed across the whole host.')
@minValue(10)
param requestRateLimitPerFiveMinutes int = 1000

resource profile 'Microsoft.Cdn/profiles@2024-02-01' existing = {
  name: profileName
}

resource endpoint 'Microsoft.Cdn/profiles/afdEndpoints@2024-02-01' = {
  parent: profile
  name: 'fde-devbrain-${resourceToken}'
  location: 'global'
  properties: {
    enabledState: 'Enabled'
  }
}

// A single origin gains nothing from Front Door health probes, and the probe traffic would keep a
// scale-to-zero Container App awake, so the origin group leaves probes off.
resource originGroup 'Microsoft.Cdn/profiles/originGroups@2024-02-01' = {
  parent: profile
  name: 'og-devbrain-${resourceToken}'
  properties: {
    loadBalancingSettings: {
      sampleSize: 4
      successfulSamplesRequired: 3
      additionalLatencyInMilliseconds: 50
    }
    sessionAffinityState: 'Disabled'
  }
}

resource origin 'Microsoft.Cdn/profiles/originGroups/origins@2024-02-01' = {
  parent: originGroup
  name: 'containerapp'
  properties: {
    hostName: originHostName
    originHostHeader: originHostName
    httpPort: 80
    httpsPort: 443
    priority: 1
    weight: 1000
    enabledState: 'Enabled'
    enforceCertificateNameCheck: true
  }
}

resource customDomain 'Microsoft.Cdn/profiles/customDomains@2024-02-01' = {
  parent: profile
  name: replace(customDomainName, '.', '-')
  properties: {
    hostName: customDomainName
    tlsSettings: {
      certificateType: 'ManagedCertificate'
      minimumTlsVersion: 'TLS12'
    }
    azureDnsZone: empty(dnsZoneId) ? null : {
      id: dnsZoneId
    }
  }
}

// No cacheConfiguration: every MCP and OAuth response is per-caller, so caching stays off.
resource route 'Microsoft.Cdn/profiles/afdEndpoints/routes@2024-02-01' = {
  parent: endpoint
  name: 'route-devbrain'
  properties: {
    customDomains: [
      { id: customDomain.id }
    ]
    originGroup: {
      id: originGroup.id
    }
    supportedProtocols: ['Http', 'Https']
    patternsToMatch: ['/*']
    forwardingProtocol: 'HttpsOnly'
    linkToDefaultDomain: 'Disabled'
    httpsRedirect: 'Enabled'
    enabledState: 'Enabled'
  }
  dependsOn: [
    origin
  ]
}

// ─── WAF policy ──────────────────────────────────────────────────────────────
//
// Custom rules only, so the policy works on the Standard tier. RequestUri patterns accept an
// optional scheme and host prefix so they match whether Front Door presents the full URL or the path.

var allowedPathPattern = '^(?:https?://[^/]+)?/(?:(?:mcp|register|authorize|callback|token|healthz)(?:[/?]|$)|\\.well-known/)'
var oauthPathPattern = '^(?:https?://[^/]+)?/(?:register|authorize|callback|token)(?:[/?]|$)'

var geoRules = empty(allowedCountryCodes) ? [] : [
  {
    name: 'BlockOutsideAllowedCountries'
    priority: 10
    enabledState: 'Enabled'
    ruleType: 'MatchRule'
    action: 'Block'
    matchConditions: [
      {
        matchVariable: 'SocketAddr'
        operator: 'GeoMatch'
        negateCondition: true
        matchValue: allowedCountryCodes
      }
    ]
  }
]

var baseRules = [
  {
    name: 'BlockUnknownPaths'
    priority: 20
    enabledState: 'Enabled'
    ruleType: 'MatchRule'
    action: 'Block'
    matchConditions: [
      {
        matchVariable: 'RequestUri'
        operator: 'RegEx'
        negateCondition: true
        matchValue: [allowedPathPattern]
        transforms: ['Lowercase']
      }
    ]
  }
  {
    // /register is open by design (RFC 7591), so cap how fast one client IP can create records.
    name: 'RateLimitOAuth'
    priority: 30
    enabledState: 'Enabled'
    ruleType: 'RateLimitRule'
    rateLimitDurationInMinutes: 1
    rateLimitThreshold: oauthRateLimitPerMinute
    action: 'Block'
    matchConditions: [
      {
        matchVariable: 'RequestUri'
        operator: 'RegEx'
        negateCondition: false
        matchValue: [oauthPathPattern]
        transforms: ['Lowercase']
      }
    ]
  }
  {
    // Every valid request has a Host header, so this matches all traffic.
    name: 'RateLimitAll'
    priority: 40
    enabledState: 'Enabled'
    ruleType: 'RateLimitRule'
    rateLimitDurationInMinutes: 5
    rateLimitThreshold: requestRateLimitPerFiveMinutes
    action: 'Block'
    matchConditions: [
      {
        matchVariable: 'RequestHeader'
        selector: 'Host'
        operator: 'GreaterThan'
        negateCondition: false
        matchValue: ['0']
      }
    ]
  }
]

resource wafPolicy 'Microsoft.Network/FrontDoorWebApplicationFirewallPolicies@2024-02-01' = {
  name: 'wafdevbrain${resourceToken}'
  location: 'Global'
  sku: {
    name: skuName
  }
  properties: {
    policySettings: {
      enabledState: 'Enabled'
      mode: 'Prevention'
      // No rule inspects the body, and skipping inspection avoids size-based rejections of large documents.
      requestBodyCheck: 'Disabled'
      customBlockResponseStatusCode: 403
    }
    customRules: {
      rules: concat(geoRules, baseRules)
    }
  }
}

resource securityPolicy 'Microsoft.Cdn/profiles/securityPolicies@2024-02-01' = {
  parent: profile
  name: 'sp-devbrain-${resourceToken}'
  properties: {
    parameters: {
      type: 'WebApplicationFirewall'
      wafPolicy: {
        id: wafPolicy.id
      }
      associations: [
        {
          domains: [
            { id: customDomain.id }
          ]
          patternsToMatch: ['/*']
        }
      ]
    }
  }
}

output frontDoorId string = profile.properties.frontDoorId
output endpointHostName string = endpoint.properties.hostName
output customDomainValidationToken string = customDomain.properties.validationProperties.validationToken
