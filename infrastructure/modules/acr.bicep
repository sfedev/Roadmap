// =============================================================================
// Azure Container Registry.
//
// Es el registro privado del que AKS y Container Apps descargan las imágenes. La autenticación
// se resuelve con IDENTIDAD GESTIONADA (rol AcrPull), no con usuario y contraseña: así no hay
// ninguna credencial de registro que rotar ni que guardar en un Secret.
// =============================================================================

@description('Nombre del registro. Global, solo minúsculas y sin guiones.')
@minLength(5)
@maxLength(50)
param acrName string

param location string
param tags object

@description('SKU. Premium añade replicación geográfica, enlaces privados y confianza de contenido.')
@allowed(['Basic', 'Standard', 'Premium'])
param sku string = 'Basic'

@description('Workspace de Log Analytics al que enviar los diagnósticos.')
param logAnalyticsWorkspaceId string

resource acr 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: acrName
  location: location
  tags: tags
  sku: {
    name: sku
  }
  properties: {
    // Usuario administrador DESACTIVADO. Es una credencial compartida, de larga vida y con
    // permisos totales sobre el registro: exactamente lo que una identidad gestionada evita.
    // Activarlo es el atajo más común y la vulnerabilidad más habitual en un ACR.
    adminUserEnabled: false
    // Purga de imágenes sin etiquetar (dangling) a los 7 días. Sin esto, cada build deja atrás
    // capas huérfanas que se pagan mes a mes para siempre.
    policies: {
      retentionPolicy: {
        status: sku == 'Premium' ? 'enabled' : 'disabled'
        days: 7
      }
      // Impide sobrescribir una etiqueta ya publicada: la versión 1.4.2 será siempre el mismo
      // binario. Solo disponible en Premium.
      quarantinePolicy: {
        status: 'disabled'
      }
    }
    // Cifrado en reposo con claves gestionadas por Microsoft (el valor por defecto).
    encryption: {
      status: 'disabled'
    }
    // Con Premium, el acceso público se cerraría y se usaría un Private Endpoint. En Basic no
    // está disponible, así que el registro es accesible por internet y protegido por Entra ID.
    publicNetworkAccess: 'Enabled'
    // Rechaza autenticación que no sea por Entra ID (tokens de identidad gestionada).
    anonymousPullEnabled: false
  }
}

// --- Diagnósticos ---------------------------------------------------------------
// Registra quién descarga y publica imágenes: es la traza de auditoría del registro.
resource acrDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'acr-diagnostics'
  // scope apunta al recurso al que se adjunta la configuración; es el patrón de los recursos
  // de extensión en Bicep.
  scope: acr
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [
      {
        // categoryGroup 'audit' agrupa los eventos de autenticación y de push/pull sin tener
        // que enumerar cada categoría a mano (y sin romperse si Azure añade alguna nueva).
        categoryGroup: 'audit'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

@description('Id del recurso; lo necesita la asignación del rol AcrPull a AKS.')
output acrId string = acr.id

@description('Servidor de inicio de sesión, para docker push y para las referencias de imagen.')
output loginServer string = acr.properties.loginServer
