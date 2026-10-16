{{/*
Expand the name of the chart.
*/}}
{{- define "daprmq.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Create a default fully qualified app name.
*/}}
{{- define "daprmq.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- $name := default .Chart.Name .Values.nameOverride }}
{{- if contains $name .Release.Name }}
{{- .Release.Name | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}
{{- end }}

{{/*
Create chart name and version as used by the chart label.
*/}}
{{- define "daprmq.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Common labels
*/}}
{{- define "daprmq.labels" -}}
helm.sh/chart: {{ include "daprmq.chart" . }}
{{ include "daprmq.selectorLabels" . }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{/*
Selector labels
*/}}
{{- define "daprmq.selectorLabels" -}}
app.kubernetes.io/name: {{ include "daprmq.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{/*
Worker labels
*/}}
{{- define "daprmq.worker.labels" -}}
{{ include "daprmq.labels" . }}
app.kubernetes.io/component: worker
{{- end }}

{{/*
Worker selector labels
*/}}
{{- define "daprmq.worker.selectorLabels" -}}
{{ include "daprmq.selectorLabels" . }}
app.kubernetes.io/component: worker
{{- end }}

{{/*
Gateway labels
*/}}
{{- define "daprmq.gateway.labels" -}}
{{ include "daprmq.labels" . }}
app.kubernetes.io/component: gateway
{{- end }}

{{/*
Gateway selector labels
*/}}
{{- define "daprmq.gateway.selectorLabels" -}}
{{ include "daprmq.selectorLabels" . }}
app.kubernetes.io/component: gateway
{{- end }}

{{/*
Operator labels
*/}}
{{- define "daprmq.operator.labels" -}}
{{ include "daprmq.labels" . }}
app.kubernetes.io/component: operator
{{- end }}

{{/*
Operator selector labels
*/}}
{{- define "daprmq.operator.selectorLabels" -}}
{{ include "daprmq.selectorLabels" . }}
app.kubernetes.io/component: operator
{{- end }}

{{/*
Console labels (console.separateDeployment)
*/}}
{{- define "daprmq.console.labels" -}}
{{ include "daprmq.labels" . }}
app.kubernetes.io/component: console
{{- end }}

{{- define "daprmq.console.selectorLabels" -}}
{{ include "daprmq.selectorLabels" . }}
app.kubernetes.io/component: console
{{- end }}

{{/*
Console env, on whichever pods serve it
*/}}
{{- define "daprmq.console.env" -}}
- name: CONSOLE_ENABLED
  value: "true"
- name: CONSOLE_CACHE_TTL_MS
  value: {{ .Values.console.cacheTtlMs | quote }}
- name: CONSOLE_MAX_WORKER_CALLS
  value: {{ .Values.console.maxWorkerCalls | quote }}
- name: SCALING_SAMPLE_INTERVAL_MS
  value: {{ mul .Values.console.scaling.sampleIntervalSeconds 1000 | quote }}
- name: SCALING_HISTORY_MINUTES
  value: {{ .Values.console.scaling.historyMinutes | quote }}
- name: KUBERNETES_READ
  value: {{ .Values.console.kubernetesRead | quote }}
{{- end }}

{{/*
Service account of the pods serving the console, when console.kubernetesRead grants it read access
to ScaledObjects and HPAs.
*/}}
{{- define "daprmq.console.serviceAccountName" -}}
{{ include "daprmq.fullname" . }}-console
{{- end }}

{{/*
Operator image reference (falls back to the global registry/tag)
*/}}
{{- define "daprmq.operator.image" -}}
{{- $registry := .Values.operator.image.registry | default .Values.image.registry }}
{{- $tag := .Values.operator.image.tag | default .Values.image.tag | default .Chart.AppVersion }}
{{- if $registry }}
{{- printf "%s/%s:%s" $registry .Values.operator.image.repository $tag }}
{{- else }}
{{- printf "%s:%s" .Values.operator.image.repository $tag }}
{{- end }}
{{- end }}

{{/*
Dashboard image reference (falls back to the global registry; tag defaults to the chart appVersion)
*/}}
{{- define "daprmq.dashboard.image" -}}
{{- $registry := .Values.dashboard.image.registry | default .Values.image.registry }}
{{- $tag := .Values.dashboard.image.tag | default .Chart.AppVersion }}
{{- if $registry }}
{{- printf "%s/%s:%s" $registry .Values.dashboard.image.repository $tag }}
{{- else }}
{{- printf "%s:%s" .Values.dashboard.image.repository $tag }}
{{- end }}
{{- end }}

{{/*
Dashboard labels
*/}}
{{- define "daprmq.dashboard.labels" -}}
{{ include "daprmq.labels" . }}
app.kubernetes.io/component: dashboard
{{- end }}

{{/*
Dashboard selector labels
*/}}
{{- define "daprmq.dashboard.selectorLabels" -}}
{{ include "daprmq.selectorLabels" . }}
app.kubernetes.io/component: dashboard
{{- end }}

{{/*
Create the image reference
*/}}
{{- define "daprmq.image" -}}
{{- if .Values.image.registry }}
{{- printf "%s/%s:%s" .Values.image.registry .Values.image.repository (.Values.image.tag | default .Chart.AppVersion) }}
{{- else }}
{{- printf "%s:%s" .Values.image.repository (.Values.image.tag | default .Chart.AppVersion) }}
{{- end }}
{{- end }}

{{/*
Create the name of the service account to use
*/}}
{{- define "daprmq.serviceAccountName" -}}
{{- if .Values.serviceAccount.create }}
{{- default (include "daprmq.fullname" .) .Values.serviceAccount.name }}
{{- else }}
{{- default "default" .Values.serviceAccount.name }}
{{- end }}
{{- end }}

{{/*
Get worker port based on protocol
*/}}
{{- define "daprmq.worker.port" -}}
{{- if eq .Values.dapr.protocol "grpc" }}
{{- .Values.worker.grpcPort }}
{{- else }}
{{- .Values.worker.httpPort }}
{{- end }}
{{- end }}

{{/*
Get gateway port based on protocol
*/}}
{{- define "daprmq.gateway.port" -}}
{{- if eq .Values.dapr.protocol "grpc" }}
{{- .Values.gateway.grpcPort }}
{{- else }}
{{- .Values.gateway.httpPort }}
{{- end }}
{{- end }}

{{/*
Fail the install if dapr.stateStoreName is not an actor state store Component in
the release namespace. Without one the sidecars start healthy but every queue
call returns 503. Skipped when rendering offline (helm template, client dry-run),
where lookup returns nothing; detected by kube-system also being invisible.
*/}}
{{- define "daprmq.validateStateStore" -}}
{{- if not .Values.dapr.skipStateStoreCheck }}
{{- $name := .Values.dapr.stateStoreName }}
{{- $ns := .Release.Namespace }}
{{- $component := lookup "dapr.io/v1alpha1" "Component" $ns $name }}
{{- if $component }}
{{- $actorStore := false }}
{{- range (($component.spec).metadata | default list) }}
{{- if and (eq .name "actorStateStore") (eq (toString .value) "true") }}
{{- $actorStore = true }}
{{- end }}
{{- end }}
{{- if not $actorStore }}
{{- fail (printf "Dapr Component %q in namespace %q is not an actor state store. Add metadata actorStateStore: \"true\" to it, or set dapr.skipStateStoreCheck=true to skip this check." $name $ns) }}
{{- end }}
{{- else if (lookup "v1" "Namespace" "" "kube-system") }}
{{- fail (printf "Dapr state store Component %q was not found in namespace %q. Dapr Components are namespace-scoped: apply it to %q before installing, or set dapr.skipStateStoreCheck=true to skip this check." $name $ns $ns) }}
{{- end }}
{{- end }}
{{- end }}
