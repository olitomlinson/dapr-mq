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
