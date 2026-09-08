// Parámetros del entorno de PRODUCCIÓN.
// Las diferencias con dev que de verdad importan las decide main.bicep a partir de
// environmentName: SKU Premium en ACR, purge protection en Key Vault, zonas de disponibilidad
// y sin escalado a cero. Aquí solo se declara el entorno y sus datos propios.
using '../main.bicep'

param environmentName = 'prod'
param location = 'switzerlandnorth'
param namePrefix = 'dotnetlab'

// En producción debería ser el id de un GRUPO de Entra ID, no el de una persona: si el
// administrador se va de la empresa, los permisos siguen funcionando.
param keyVaultAdminObjectId = ''

param kubernetesVersion = '1.31.3'

// Más CPU y memoria por nodo: menos nodos para la misma capacidad y menor coste de gestión.
param systemNodeVmSize = 'Standard_D4s_v5'

param tags = {
  application: 'dotnetlab'
  environment: 'prod'
  managedBy: 'bicep'
  costCenter: 'production'
  // Etiqueta de criticidad: la usan las políticas de respaldo y de guardia.
  criticality: 'high'
}
