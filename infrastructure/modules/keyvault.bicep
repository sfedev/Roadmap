// =============================================================================
// Azure Key Vault: origen de verdad de los secretos de la aplicación.
//
// AKS los consume con el Secrets Store CSI Driver, que los monta como FICHEROS dentro del pod.
// La aplicación ya lee sus secretos por ruta (SharedKey__File, Messaging__PasswordFile), así
// que pasar de un docker secret a Key Vault no cambia una sola línea de C#: solo la ruta.
// =============================================================================

@description('Nombre del vault. Global y con un máximo de 24 caracteres.')
@minLength(3)
@maxLength(24)
param keyVaultName string

param location string
param tags object

@description('Objeto de Entra ID que recibirá el rol de administrador. Vacío = no se asigna.')
param adminObjectId string = ''

@description('Protección contra purga. Obligatoria en producción, molesta en desarrollo.')
param enablePurgeProtection bool = false

param logAnalyticsWorkspaceId string

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      // Standard guarda las claves por software; Premium usa HSM validado FIPS 140-2 nivel 2.
      // Para secretos de aplicación, Standard es suficiente.
      name: 'standard'
    }
    tenantId: tenant().tenantId

    // RBAC en lugar de las access policies clásicas. Es la recomendación actual de Microsoft:
    // los permisos se gestionan como los del resto de Azure (asignaciones de rol heredables y
    // auditables) en vez de con una lista propia del vault que nadie revisa nunca.
    enableRbacAuthorization: true

    // Borrado lógico: un secreto borrado se puede recuperar durante este periodo. Ya no se
    // puede desactivar (Azure lo impone), pero sí ajustar la ventana.
    enableSoftDelete: true
    softDeleteRetentionInDays: enablePurgeProtection ? 90 : 7

    // Con purge protection activa, ni siquiera un administrador puede borrar definitivamente el
    // vault antes de que expire la retención. Es irreversible: una vez encendida, no se apaga.
    enablePurgeProtection: enablePurgeProtection ? true : null

    // Permite que el CSI driver y otros recursos de Azure lean los secretos.
    enabledForTemplateDeployment: true

    networkAcls: {
      // 'Allow' porque el laboratorio no monta red privada. En producción esto sería 'Deny'
      // más un Private Endpoint, para que el vault no sea accesible desde internet.
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

// --- Asignación de rol ------------------------------------------------------------
// Rol "Key Vault Secrets Officer": leer, escribir y borrar secretos, pero NO gestionar el
// vault ni sus permisos. Es el mínimo necesario para quien administra los secretos.
var keyVaultSecretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'

resource adminRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(adminObjectId)) {
  // El nombre de una asignación de rol debe ser un GUID DETERMINISTA: si se generase al azar,
  // cada despliegue crearía una asignación duplicada. guid() sobre los tres identificadores
  // produce siempre el mismo valor para la misma combinación, que es lo que la hace idempotente.
  name: guid(keyVault.id, adminObjectId, keyVaultSecretsOfficerRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsOfficerRoleId)
    principalId: adminObjectId
    // 'User' por defecto. Si el objeto fuese un grupo o una identidad gestionada habría que
    // cambiarlo; declararlo evita el error transitorio de "principal no encontrado" que ocurre
    // cuando ARM asume el tipo y la réplica de Entra ID aún no propagó el objeto.
    principalType: 'User'
  }
}

// --- Diagnósticos -------------------------------------------------------------------
resource keyVaultDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'kv-diagnostics'
  scope: keyVault
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [
      {
        // AuditEvent registra CADA lectura de secreto, con quién y cuándo. En una auditoría de
        // seguridad es lo primero que se pide.
        category: 'AuditEvent'
        enabled: true
      }
    ]
  }
}

@description('Nombre del vault, para el SecretProviderClass del CSI driver.')
output keyVaultName string = keyVault.name

@description('URI del vault.')
output keyVaultUri string = keyVault.properties.vaultUri

@description('Id del recurso.')
output keyVaultId string = keyVault.id
