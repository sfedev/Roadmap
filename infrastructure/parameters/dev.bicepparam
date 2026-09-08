// Parámetros del entorno de DESARROLLO.
// El formato .bicepparam (frente al JSON clásico) tipa los parámetros contra la plantilla:
// un nombre mal escrito o un valor de tipo incorrecto falla al compilar, no al desplegar.
using '../main.bicep'

param environmentName = 'dev'
// Suiza Norte: residencia de datos en Suiza y la latencia más baja desde Zúrich o Ginebra.
param location = 'switzerlandnorth'
param namePrefix = 'dotnetlab'

// Objeto de Entra ID que administrará los secretos. Se obtiene con:
//   az ad signed-in-user show --query id -o tsv
// Vacío = no se crea la asignación de rol y el vault queda sin administradores explícitos.
param keyVaultAdminObjectId = ''

// Versión fija: sin ella, cada despliegue tomaría la versión por defecto de Azure, que cambia
// con el tiempo y actualizaría el cluster sin que nadie lo hubiera pedido.
param kubernetesVersion = '1.31.3'

// 2 vCPU y 8 GiB: el tamaño mínimo razonable para un pool de sistema de AKS.
param systemNodeVmSize = 'Standard_D2s_v5'

param tags = {
  application: 'dotnetlab'
  environment: 'dev'
  managedBy: 'bicep'
  // Etiqueta de coste: permite filtrar el gasto por entorno en Cost Management.
  costCenter: 'learning'
}
