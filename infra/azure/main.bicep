@description('Environment name: dev, staging, prod')
param environment string = 'prod'

@description('Azure region')
param location string = resourceGroup().location

@description('MySQL admin password')
@secure()
param dbPassword string

@description('SendGrid API Key')
@secure()
param sendGridKey string

@description('PayPal Client ID')
param paypalClientId string

@description('PayPal Client Secret')
@secure()
param paypalSecret string

var appName = 'ipro-${environment}'
var dbServerName = 'ipro-mysql-${environment}'
var storageAccountName = 'iprostorage${environment}'

// ── App Service Plan ──────────────────────────────────────
resource appServicePlan 'Microsoft.Web/serverfarms@2023-01-01' = {
  name: '${appName}-plan'
  location: location
  sku: { name: 'B2', tier: 'Basic', capacity: 1 }
  properties: { reserved: true }
  kind: 'linux'
}

// ── App Service (Web) ─────────────────────────────────────
resource webApp 'Microsoft.Web/sites@2023-01-01' = {
  name: '${appName}-web'
  location: location
  properties: {
    serverFarmId: appServicePlan.id
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
        { name: 'ConnectionStrings__DefaultConnection', value: 'server=${dbServerName}.mysql.database.azure.com;port=3306;database=ipro_crm;user=iproadmin;password=${dbPassword};SslMode=Required;' }
        { name: 'Email__SendGridApiKey', value: sendGridKey }
        { name: 'PayPal__ClientId', value: paypalClientId }
        { name: 'PayPal__ClientSecret', value: paypalSecret }
        { name: 'PayPal__IsSandbox', value: 'false' }
        { name: 'Azure__StorageAccountName', value: storageAccountName }
        { name: 'Azure__StorageConnectionString', value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccountName};AccountKey=${storageAccount.listKeys().keys[0].value};EndpointSuffix=core.windows.net' }
      ]
    }
    httpsOnly: true
  }
}

// ── MySQL Flexible Server ─────────────────────────────────
resource mysqlServer 'Microsoft.DBforMySQL/flexibleServers@2023-06-01-preview' = {
  name: dbServerName
  location: location
  sku: { name: 'Standard_B1ms', tier: 'Burstable' }
  properties: {
    administratorLogin: 'iproadmin'
    administratorLoginPassword: dbPassword
    storage: { storageSizeGB: 20 }
    backup: { backupRetentionDays: 7, geoRedundantBackup: 'Disabled' }
    version: '8.0.21'
  }
}

resource mysqlDatabase 'Microsoft.DBforMySQL/flexibleServers/databases@2023-06-01-preview' = {
  parent: mysqlServer
  name: 'ipro_crm'
  properties: { charset: 'utf8mb4', collation: 'utf8mb4_unicode_ci' }
}

resource mysqlFirewall 'Microsoft.DBforMySQL/flexibleServers/firewallRules@2023-06-01-preview' = {
  parent: mysqlServer
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

// ── Storage Account ───────────────────────────────────────
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: true
    minimumTlsVersion: 'TLS1_2'
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' = {
  parent: storageAccount
  name: 'default'
}

resource agentLogosContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobService
  name: 'agent-logos'
  properties: { publicAccess: 'Blob' }
}

resource agentFilesContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobService
  name: 'agent-files'
  properties: { publicAccess: 'None' }
}

// ── Outputs ───────────────────────────────────────────────
output webAppUrl string = 'https://${webApp.properties.defaultHostName}'
output dbHostName string = mysqlServer.properties.fullyQualifiedDomainName
output storageAccountName string = storageAccount.name
