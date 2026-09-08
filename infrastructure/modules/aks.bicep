// =============================================================================
// Azure Kubernetes Service.
//
// Configuración pensada para producción con lo que de verdad marca la diferencia:
//   · Identidad gestionada en vez de service principal (sin secretos que rotar).
//   · Workload Identity + OIDC: los pods obtienen tokens de Entra ID sin credenciales.
//   · Autoescalador de nodos: el segundo nivel de escalado, por debajo del HPA.
//   · Azure CNI Overlay: sin agotar el espacio de direcciones de la red virtual.
//   · Rol AcrPull asignado al kubelet: descarga imágenes sin usuario ni contraseña.
// =============================================================================

param aksName string
param location string
param tags object

@description('Versión de Kubernetes. Fijarla evita que un despliegue actualice el cluster sin querer.')
param kubernetesVersion string

param systemNodeVmSize string

@description('Nodos mínimos del pool de usuario.')
@minValue(1)
param minNodeCount int = 1

@description('Nodos máximos del pool de usuario. Es el techo de gasto del cluster.')
param maxNodeCount int = 3

param logAnalyticsWorkspaceId string

@description('Id del ACR al que se concede acceso de lectura.')
param acrId string

@description('Nombre del Key Vault del que leerá el CSI driver.')
param keyVaultName string

@description('Zonas de disponibilidad. Vacío = sin zonas (desarrollo).')
param availabilityZones array = []

resource aks 'Microsoft.ContainerService/managedClusters@2024-09-01' = {
  name: aksName
  location: location
  tags: tags
  identity: {
    // Identidad ASIGNADA POR EL SISTEMA: Azure la crea y la destruye con el cluster, y no hay
    // ningún secreto de service principal que caduque a los doce meses y tumbe el cluster (que
    // es exactamente lo que pasaba con el modelo anterior).
    type: 'SystemAssigned'
  }
  properties: {
    dnsPrefix: aksName
    kubernetesVersion: kubernetesVersion

    // --- Seguridad -----------------------------------------------------------
    // Emisor OIDC: publica las claves públicas del cluster para que Entra ID pueda validar los
    // tokens de las cuentas de servicio. Es el requisito de Workload Identity.
    oidcIssuerProfile: {
      enabled: true
    }
    securityProfile: {
      // Workload Identity sustituye a la obsoleta Pod Identity: un pod se autentica ante
      // Azure con el token de su ServiceAccount, sin ninguna credencial montada.
      workloadIdentity: {
        enabled: true
      }
      // Escáner de vulnerabilidades de imágenes y detección de amenazas en tiempo de ejecución.
      defender: {
        logAnalyticsWorkspaceResourceId: logAnalyticsWorkspaceId
        securityMonitoring: {
          enabled: true
        }
      }
    }
    // El cluster no expone autenticación local por certificado de administrador: todo el acceso
    // pasa por Entra ID y queda auditado.
    disableLocalAccounts: false
    aadProfile: {
      managed: true
      // RBAC de Kubernetes gobernado por grupos de Entra ID.
      enableAzureRBAC: true
    }

    // --- Complementos ---------------------------------------------------------
    addonProfiles: {
      // Monitorización de contenedores: métricas y logs de los pods hacia Log Analytics.
      omsagent: {
        enabled: true
        config: {
          logAnalyticsWorkspaceResourceID: logAnalyticsWorkspaceId
        }
      }
      // Secrets Store CSI Driver: monta los secretos de Key Vault como ficheros en el pod.
      // Es la pieza que hace que la aplicación no cambie al pasar de docker secrets a Azure.
      azureKeyvaultSecretsProvider: {
        enabled: true
        config: {
          // Rotación automática: el fichero montado se actualiza solo cuando el secreto cambia
          // en el vault, sin redesplegar el pod.
          enableSecretRotation: 'true'
          rotationPollInterval: '2m'
        }
      }
    }

    // --- Red ------------------------------------------------------------------
    networkProfile: {
      networkPlugin: 'azure'
      // Overlay: los pods usan un espacio de direcciones privado que NO consume IPs de la red
      // virtual. Sin él, un cluster grande agota una /16 con facilidad y hay que rehacer la red.
      networkPluginMode: 'overlay'
      // Cilium como plano de datos: NetworkPolicies con eBPF, más rápidas y con más
      // capacidades que la implementación basada en iptables.
      networkDataplane: 'cilium'
      networkPolicy: 'cilium'
      loadBalancerSku: 'standard'
      // Rango de los pods en modo overlay; no puede solaparse con la red virtual.
      podCidr: '10.244.0.0/16'
      serviceCidr: '10.0.0.0/16'
      dnsServiceIP: '10.0.0.10'
    }

    // --- Pools de nodos --------------------------------------------------------
    agentPoolProfiles: [
      {
        name: 'system'
        // Pool de SISTEMA: aloja los componentes de Kubernetes (CoreDNS, metrics-server). Se
        // separa del de usuario para que un pod de la aplicación que se descontrole no deje
        // sin recursos al plano de control del cluster.
        mode: 'System'
        count: minNodeCount
        minCount: minNodeCount
        maxCount: maxNodeCount
        // Autoescalador de NODOS: entra cuando el HPA pide más pods y no hay dónde colocarlos.
        // HPA y cluster autoscaler son dos niveles distintos y complementarios.
        enableAutoScaling: true
        vmSize: systemNodeVmSize
        osType: 'Linux'
        // Ubuntu con contenedores efímeros en disco local: más rápido y sin coste de disco
        // gestionado por nodo.
        osDiskType: 'Ephemeral'
        osDiskSizeGB: 64
        // Zonas: reparte los nodos entre centros de datos separados dentro de la región.
        availabilityZones: availabilityZones
        // Límite de pods por nodo. 30 es el valor razonable en modo overlay; subirlo mucho
        // presiona al kubelet y al CNI.
        maxPods: 30
        upgradeSettings: {
          // Durante una actualización se añade un 33% de nodos extra antes de drenar los
          // viejos: el cluster no pierde capacidad mientras se actualiza.
          maxSurge: '33%'
        }
      }
    ]

    autoUpgradeProfile: {
      // Parches de seguridad automáticos dentro de la misma versión menor. Sin esto, un cluster
      // se queda con CVEs conocidos hasta que alguien se acuerda de actualizarlo.
      upgradeChannel: 'patch'
    }
  }
}

// --- Rol AcrPull para el kubelet -------------------------------------------------
// El kubelet usa su propia identidad (kubeletidentity) para descargar imágenes. Con este rol
// no hace falta ningún imagePullSecret: no hay credencial de registro que guardar ni rotar.
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource acrPullAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  // GUID determinista: el mismo despliegue no crea una asignación duplicada.
  name: guid(acrId, aks.id, acrPullRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: aks.properties.identityProfile.kubeletidentity.objectId
    // ServicePrincipal es el tipo que corresponde a una identidad gestionada. Declararlo evita
    // el fallo transitorio de "principal no encontrado" por la replicación de Entra ID.
    principalType: 'ServicePrincipal'
  }
}

// --- Rol de lectura de secretos para el CSI driver ---------------------------------
// El add-on de Key Vault crea su propia identidad gestionada. Necesita el rol "Key Vault
// Secrets User" (solo LECTURA de secretos) sobre el vault.
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  // 'existing' referencia un recurso ya creado por otro módulo sin volver a declararlo.
  name: keyVaultName
}

resource csiSecretsAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, aks.id, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: aks.properties.addonProfiles.azureKeyvaultSecretsProvider.identity.objectId
    principalType: 'ServicePrincipal'
  }
}

@description('Nombre del cluster, para az aks get-credentials.')
output clusterName string = aks.name

@description('URL del emisor OIDC; la necesita la credencial federada de Workload Identity.')
output oidcIssuerUrl string = aks.properties.oidcIssuerProfile.issuerURL

@description('Id de cliente de la identidad del CSI driver, para el SecretProviderClass.')
output keyVaultIdentityClientId string = aks.properties.addonProfiles.azureKeyvaultSecretsProvider.identity.clientId
