// =============================================================================
// Log Analytics + Application Insights.
//
// Se despliega el primero porque todos los demás módulos envían aquí sus diagnósticos.
// =============================================================================

@description('Nombre del workspace de Log Analytics.')
param logAnalyticsName string

@description('Nombre del recurso de Application Insights.')
param appInsightsName string

param location string
param tags object

@description('Días de retención. Es el principal factor de coste del workspace.')
@minValue(30)
@maxValue(730)
param retentionInDays int = 30

// --- Log Analytics -------------------------------------------------------------
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      // PerGB2018 es el modelo de pago por uso. Los SKU de capacidad reservada salen a cuenta
      // a partir de unos 100 GB/día, muy por encima de lo que genera este laboratorio.
      name: 'PerGB2018'
    }
    retentionInDays: retentionInDays
    features: {
      // Impide que otros recursos se enlacen a este workspace sin permiso explícito.
      disableLocalAuth: false
    }
    workspaceCapping: {
      // Tope diario de ingesta. Es la red de seguridad contra la factura sorpresa: un bucle de
      // logs en un pod puede generar cientos de GB en una noche.
      dailyQuotaGb: 5
    }
  }
}

// --- Application Insights ------------------------------------------------------
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    // Modo "workspace-based" (obligatorio desde 2024): los datos viven en Log Analytics, no en
    // el almacén clásico de App Insights. Permite correlacionar en una sola consulta KQL las
    // trazas de la aplicación con los logs de los contenedores de AKS.
    WorkspaceResourceId: logAnalytics.id
    IngestionMode: 'LogAnalytics'
    // Deshabilita la ingesta por clave de instrumentación: obliga a usar Entra ID.
    DisableLocalAuth: false
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

@description('Id del workspace, para enlazar los diagnósticos del resto de recursos.')
output workspaceId string = logAnalytics.id

@description('GUID del workspace (customerId); lo necesita el add-on de Container Apps.')
output customerId string = logAnalytics.properties.customerId

@description('Cadena de conexión de Application Insights.')
// @secure() evita que el valor quede visible en el historial de despliegues.
@secure()
output appInsightsConnectionString string = appInsights.properties.ConnectionString
