{{/*
=============================================================================
Helpers del chart. Centralizan el cálculo de nombres y etiquetas para que las plantillas no
repitan la misma expresión quince veces (y para que corregir una convención sea tocar un
único sitio).
=============================================================================
*/}}

{{/*
Nombre base del chart, recortado a 63 caracteres porque ese es el límite de una etiqueta de
Kubernetes. trimSuffix "-" evita quedarse con un guion colgando si el corte cae justo ahí.
*/}}
{{- define "dotnetlab.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
Nombre completo de la release. Si el usuario ya llamó a la release como el chart
(helm install dotnetlab ./dotnetlab), no se duplica en "dotnetlab-dotnetlab".
*/}}
{{- define "dotnetlab.fullname" -}}
{{- if contains .Chart.Name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name .Chart.Name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}

{{/*
Etiquetas comunes a TODOS los recursos. Siguen la convención app.kubernetes.io/*, que es lo
que usan kubectl, Helm y las herramientas de observabilidad para agrupar objetos.
*/}}
{{- define "dotnetlab.labels" -}}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
app.kubernetes.io/name: {{ include "dotnetlab.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/part-of: dotnetlab
{{- end -}}

{{/*
Etiquetas de SELECCIÓN de un componente. Se mantienen aparte de las comunes a propósito: el
selector de un Deployment es INMUTABLE, así que no puede incluir la versión del chart (que
cambia en cada release) o cada actualización obligaría a borrar y recrear el Deployment.

Recibe un diccionario con la raíz y el nombre del componente:
  include "dotnetlab.selectorLabels" (dict "root" $ "component" $name)
*/}}
{{- define "dotnetlab.selectorLabels" -}}
app.kubernetes.io/name: {{ .component }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
{{- end -}}

{{/*
Nombre de un componente concreto: "<release>-api", "<release>-web"...
*/}}
{{- define "dotnetlab.componentName" -}}
{{- printf "%s-%s" (include "dotnetlab.fullname" .root) .component | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
Referencia completa de la imagen de un componente.
La etiqueta cae en cascada: image.tag del values, y si está vacío, appVersion del chart. Así
la versión desplegada queda siempre atada a algo versionado, nunca a un "latest" implícito.
*/}}
{{- define "dotnetlab.image" -}}
{{- $registry := .root.Values.image.registry -}}
{{- $tag := .root.Values.image.tag | default .root.Chart.AppVersion -}}
{{- if $registry -}}
{{- printf "%s/%s:%s" $registry .config.image $tag -}}
{{- else -}}
{{- printf "%s:%s" .config.image $tag -}}
{{- end -}}
{{- end -}}

{{/*
Nombre del Secret que montan los pods: el existente si se indicó uno, o el que crea el chart.
*/}}
{{- define "dotnetlab.secretName" -}}
{{- if .Values.secrets.existingSecret -}}
{{- .Values.secrets.existingSecret -}}
{{- else -}}
{{- printf "%s-secrets" (include "dotnetlab.fullname" .) -}}
{{- end -}}
{{- end -}}
