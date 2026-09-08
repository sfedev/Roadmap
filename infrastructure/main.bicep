// =============================================================================
// DotNetLab — infraestructura en Azure.
//
// Ámbito: GRUPO DE RECURSOS. Se elige frente al ámbito de suscripción porque quien despliega
// no necesita permisos de Owner sobre la suscripción entera: basta con Contributor sobre un
// grupo, que es el mínimo privilegio razonable para un pipeline.
//
//   az group create -n rg-dotnetlab-dev -l switzerlandnorth
//   az deployment group create -g rg-dotnetlab-dev -f infrastructure/main.bicep \
//     -p infrastructure/parameters/dev.bicepparam
// =============================================================================

// targetScope explícito: sin él, Bicep asume 'resourceGroup' igualmente, pero dejarlo escrito
// evita que un cambio futuro a recursos de suscripción pase desapercibido en la revisión.
targetScope = 'resourceGroup'

// --- Parámetros --------------------------------------------------------------

@description('Nombre corto del entorno; forma parte del nombre de todos los recursos.')
// La restricción de valores la valida ARM ANTES de crear nada: un typo no llega a desplegar.
@allowed(['dev', 'stg', 'prod'])
param environmentName string = 'dev'

@description('Región de Azure. Suiza Norte por residencia de datos y por latencia desde Zúrich.')
param location string = resourceGroup().location

@description('Prefijo de nombres. Máximo 12 caracteres: los sufijos generados consumen el resto.')
@minLength(3)
@maxLength(12)
param namePrefix string = 'dotnetlab'

@description('Objeto de Entra ID (usuario o grupo) que recibirá el rol de administrador del Key Vault.')
param keyVaultAdminObjectId string = ''

@description('Versión de Kubernetes del cluster de AKS.')
param kubernetesVersion string = '1.31.3'

@description('Tamaño de VM del pool de sistema.')
param systemNodeVmSize string = 'Standard_D2s_v5'

@description('Etiquetas aplicadas a todos los recursos.')
param tags object = {
  application: 'dotnetlab'
  environment: environmentName
  managedBy: 'bicep'
}

// --- Variables ---------------------------------------------------------------

// uniqueString genera un hash DETERMINISTA a partir del id del grupo de recursos: el mismo
// despliegue produce siempre el mismo nombre (idempotencia), pero dos suscripciones distintas
// producen nombres distintos. Es la forma canónica de resolver los nombres globalmente únicos
// que exigen ACR y Key Vault.
var uniqueSuffix = uniqueString(resourceGroup().id)

// ACR no admite guiones ni mayúsculas en el nombre: hay que concatenar en minúscula y limpio.
var acrName = toLower('${namePrefix}${environmentName}${take(uniqueSuffix, 6)}')
// Key Vault tiene un máximo de 24 caracteres; 'take' recorta para no pasarse.
var keyVaultName = take('kv-${namePrefix}-${environmentName}-${take(uniqueSuffix, 5)}', 24)
var aksName = 'aks-${namePrefix}-${environmentName}'
var logAnalyticsName = 'log-${namePrefix}-${environmentName}'
var appInsightsName = 'appi-${namePrefix}-${environmentName}'
var containerAppEnvName = 'cae-${namePrefix}-${environmentName}'

// Producción escala y protege más. Concentrar la diferencia en una variable evita repetir el
// mismo ternario en cada recurso, que es donde se cuelan las incoherencias entre entornos.
var isProduction = environmentName == 'prod'

// --- Observabilidad -----------------------------------------------------------
// Se despliega PRIMERO porque el resto de módulos envían sus diagnósticos aquí.

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring-deployment'
  params: {
    logAnalyticsName: logAnalyticsName
    appInsightsName: appInsightsName
    location: location
    tags: tags
    // 90 días en producción frente a 30 en desarrollo: la retención es el principal factor de
    // coste de Log Analytics, y en dev nadie investiga un incidente de hace dos meses.
    retentionInDays: isProduction ? 90 : 30
  }
}

// --- Registro de contenedores --------------------------------------------------

module registry 'modules/acr.bicep' = {
  name: 'acr-deployment'
  params: {
    acrName: acrName
    location: location
    tags: tags
    // Premium solo en producción: es el único SKU con replicación geográfica, enlaces privados
    // y confianza de contenido. En dev, Basic sobra y cuesta una fracción.
    sku: isProduction ? 'Premium' : 'Basic'
    logAnalyticsWorkspaceId: monitoring.outputs.workspaceId
  }
}

// --- Key Vault -----------------------------------------------------------------

module vault 'modules/keyvault.bicep' = {
  name: 'keyvault-deployment'
  params: {
    keyVaultName: keyVaultName
    location: location
    tags: tags
    adminObjectId: keyVaultAdminObjectId
    // Purge protection impide borrar definitivamente un secreto durante el periodo de
    // retención. En producción es obligatorio; en dev estorba, porque impide reutilizar el
    // nombre del vault al recrear el entorno.
    enablePurgeProtection: isProduction
    logAnalyticsWorkspaceId: monitoring.outputs.workspaceId
  }
}

// --- AKS -------------------------------------------------------------------------

module aks 'modules/aks.bicep' = {
  name: 'aks-deployment'
  params: {
    aksName: aksName
    location: location
    tags: tags
    kubernetesVersion: kubernetesVersion
    systemNodeVmSize: systemNodeVmSize
    // El autoescalador de nodos entra cuando el HPA ya no encuentra sitio donde colocar pods:
    // son dos niveles distintos y complementarios de escalado.
    minNodeCount: isProduction ? 3 : 1
    maxNodeCount: isProduction ? 10 : 3
    logAnalyticsWorkspaceId: monitoring.outputs.workspaceId
    acrId: registry.outputs.acrId
    keyVaultName: vault.outputs.keyVaultName
    // Zonas de disponibilidad solo en producción: reparten los nodos entre centros de datos
    // físicamente separados dentro de la región.
    availabilityZones: isProduction ? ['1', '2', '3'] : []
  }
}

// --- Container Apps (alternativa serverless) ---------------------------------------
// Se despliega para poder COMPARAR: la misma API sobre AKS y sobre Container Apps. Container
// Apps abstrae el cluster (no hay nodos que parchear) y escala a cero, pero deja fuera todo
// lo que exige acceso al plano de control: DaemonSets, operadores, CRDs o KEDA a medida.

module containerApps 'modules/containerapps.bicep' = {
  name: 'containerapps-deployment'
  params: {
    environmentName: containerAppEnvName
    location: location
    tags: tags
    logAnalyticsWorkspaceId: monitoring.outputs.workspaceId
    logAnalyticsCustomerId: monitoring.outputs.customerId
    acrLoginServer: registry.outputs.loginServer
    acrId: registry.outputs.acrId
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    // En producción no escala a cero: el arranque en frío de una API de cara al usuario es
    // inaceptable. En dev sí, y el entorno cuesta cero cuando nadie lo usa.
    minReplicas: isProduction ? 2 : 0
    maxReplicas: isProduction ? 10 : 3
  }
}

// --- Salidas ------------------------------------------------------------------------
// Las consume el pipeline de CI/CD para no tener que codificar nombres a mano.

@description('Servidor de inicio de sesión del ACR (para docker push).')
output acrLoginServer string = registry.outputs.loginServer

@description('Nombre del cluster de AKS (para az aks get-credentials).')
output aksClusterName string = aks.outputs.clusterName

@description('Emisor OIDC del cluster; lo necesita la federación de Workload Identity.')
output aksOidcIssuerUrl string = aks.outputs.oidcIssuerUrl

@description('Nombre del Key Vault donde viven los secretos de la aplicación.')
output keyVaultName string = vault.outputs.keyVaultName

@description('Id de cliente de la identidad gestionada que usa el CSI driver para leer el vault.')
output keyVaultIdentityClientId string = aks.outputs.keyVaultIdentityClientId

@description('Cadena de conexión de Application Insights.')
// @secure() en una salida impide que el valor aparezca en el historial del despliegue, que es
// consultable por cualquiera con permiso de lectura sobre el grupo de recursos.
@secure()
output appInsightsConnectionString string = monitoring.outputs.appInsightsConnectionString

@description('FQDN público de la API desplegada en Container Apps.')
output containerAppApiFqdn string = containerApps.outputs.apiFqdn
