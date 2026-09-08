// =============================================================================
// Azure Container Apps: la alternativa SIN cluster que gestionar.
//
// Se despliega junto a AKS para poder comparar los dos modelos con la misma aplicación:
//
//   Container Apps  · No hay nodos que parchear ni versión de Kubernetes que actualizar.
//                   · Escala a CERO: fuera de horas, el entorno de desarrollo cuesta 0 €.
//                   · Escalado por eventos (KEDA integrado) sin instalar nada.
//                   · A cambio: sin acceso al plano de control. Nada de DaemonSets,
//                     operadores, CRDs, ni mallas de servicio propias.
//
//   AKS             · Control total y portabilidad: los mismos manifiestos valen en cualquier
//                     Kubernetes conforme.
//                   · A cambio: hay que operar el cluster (versiones, nodos, complementos).
//
// Para esta API, Container Apps cubre el 90% de las necesidades con una fracción del trabajo
// operativo. AKS se mantiene porque el objetivo del repositorio incluye practicar Kubernetes.
// =============================================================================

param environmentName string
param location string
param tags object

param logAnalyticsWorkspaceId string

@description('GUID del workspace (customerId), distinto del id del recurso.')
param logAnalyticsCustomerId string

param acrLoginServer string
param acrId string

@secure()
param appInsightsConnectionString string

@description('Réplicas mínimas. 0 permite escalar a cero (con arranque en frío).')
@minValue(0)
param minReplicas int = 0

@minValue(1)
param maxReplicas int = 3

// --- Identidad gestionada -------------------------------------------------------
// Identidad ASIGNADA POR EL USUARIO (y no por el sistema) porque debe existir ANTES que la
// aplicación: el rol AcrPull tiene que estar concedido en el momento en que la app intenta
// descargar su primera imagen, o el despliegue falla con un error de autenticación.
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${environmentName}'
  location: location
  tags: tags
}

var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource acrPullAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acrId, identity.id, acrPullRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// --- Entorno de Container Apps ---------------------------------------------------
// Equivale al cluster: da la red compartida, el DNS interno y el destino de los logs para
// todas las aplicaciones que contiene.
resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: environmentName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsCustomerId
        // La clave compartida se lee del propio workspace con listKeys(): así no hay que
        // pasarla como parámetro ni guardarla en ningún sitio.
        sharedKey: listKeys(logAnalyticsWorkspaceId, '2023-09-01').primarySharedKey
      }
    }
    // Deja que Azure asigne el rango de la red. En un entorno con red virtual propia aquí
    // iría vnetConfiguration con la subred delegada.
    zoneRedundant: false
  }
}

// --- Aplicación: la API ------------------------------------------------------------
resource apiApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-dotnetlab-api'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      // La sintaxis de clave dinámica exige esta forma; el valor vacío es lo que espera ARM.
      '${identity.id}': {}
    }
  }
  properties: {
    environmentId: environment.id
    configuration: {
      ingress: {
        // external: accesible desde internet. Con false, solo desde el propio entorno.
        external: true
        // Puerto del contenedor: el mismo 8080 de las imágenes.
        targetPort: 8080
        transport: 'auto'
        // TLS lo termina la plataforma con su propio certificado: no hay nada que gestionar.
        allowInsecure: false
      }
      registries: [
        {
          server: acrLoginServer
          // Autenticación por identidad gestionada, sin usuario ni contraseña de registro.
          identity: identity.id
        }
      ]
      secrets: [
        {
          name: 'appinsights-connection'
          value: appInsightsConnectionString
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          // La etiqueta la sustituye el pipeline en cada despliegue.
          image: '${acrLoginServer}/dotnetlab-api:latest'
          resources: {
            // Container Apps solo admite combinaciones concretas de CPU y memoria; 0,5 vCPU
            // con 1 GiB es una de las válidas. Otra proporción da error de validación.
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              // secretRef en vez de value: el valor no aparece en la definición del recurso.
              secretRef: 'appinsights-connection'
            }
            {
              // Container Apps inyecta su propio colector OTLP en el entorno.
              name: 'OTEL_EXPORTER_OTLP_ENDPOINT'
              value: 'http://localhost:4317'
            }
          ]
          probes: [
            {
              // Mismas rutas que en Kubernetes: ServiceDefaults las expone igual en los tres
              // servicios, así que la aplicación no sabe en qué plataforma corre.
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: 8080
              }
              periodSeconds: 10
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: 8080
              }
              periodSeconds: 5
            }
            {
              type: 'Startup'
              httpGet: {
                path: '/health/live'
                port: 8080
              }
              periodSeconds: 2
              failureThreshold: 30
            }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: [
          {
            name: 'http-concurrency'
            http: {
              metadata: {
                // Una réplica más por cada 50 peticiones concurrentes. Es KEDA por debajo, pero
                // sin instalar nada: en AKS habría que desplegar el operador aparte.
                concurrentRequests: '50'
              }
            }
          }
        ]
      }
    }
  }
  // La asignación de rol debe existir ANTES de crear la app, o el primer pull falla. Bicep no
  // puede deducir esta dependencia porque no hay referencia directa entre los dos recursos.
  dependsOn: [
    acrPullAssignment
  ]
}

@description('FQDN público de la API en Container Apps.')
output apiFqdn string = apiApp.properties.configuration.ingress.fqdn

@description('Id del entorno, por si se añaden más aplicaciones.')
output environmentId string = environment.id
