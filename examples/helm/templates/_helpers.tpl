{{/*
Expand the name of the chart.
*/}}
{{- define "daprmq-examples.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Create a default fully qualified app name.
*/}}
{{- define "daprmq-examples.fullname" -}}
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
{{- define "daprmq-examples.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Common labels
*/}}
{{- define "daprmq-examples.labels" -}}
helm.sh/chart: {{ include "daprmq-examples.chart" . }}
{{ include "daprmq-examples.selectorLabels" . }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{/*
Selector labels
*/}}
{{- define "daprmq-examples.selectorLabels" -}}
app.kubernetes.io/name: {{ include "daprmq-examples.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{/*
Namespace the main daprmq chart's gateway Service lives in.
*/}}
{{- define "daprmq-examples.gatewayNamespace" -}}
{{- default .Release.Namespace .Values.daprmq.namespace }}
{{- end }}

{{/*
Name of the main daprmq chart's gateway Service. Replicates that chart's own
`daprmq.fullname` helper logic (releaseName as-is if it already contains "daprmq",
otherwise "<releaseName>-daprmq") plus its fixed "-gateway" suffix, so this chart
works out of the box against a same-named default install without depending on the
other chart's templates. `daprmq.gatewayServiceName` overrides this outright.
*/}}
{{- define "daprmq-examples.gatewayServiceName" -}}
{{- if .Values.daprmq.gatewayServiceName }}
{{- .Values.daprmq.gatewayServiceName }}
{{- else if contains "daprmq" .Values.daprmq.releaseName }}
{{- printf "%s-gateway" .Values.daprmq.releaseName }}
{{- else }}
{{- printf "%s-daprmq-gateway" .Values.daprmq.releaseName }}
{{- end }}
{{- end }}

{{/*
Default in-cluster HTTP base URL for the DaprMQ gateway (short form, same-namespace).
*/}}
{{- define "daprmq-examples.gatewayHttpUrl" -}}
http://{{ include "daprmq-examples.gatewayServiceName" . }}.{{ include "daprmq-examples.gatewayNamespace" . }}.svc.cluster.local:{{ .Values.daprmq.httpPort }}
{{- end }}

{{/*
Default in-cluster gRPC address for the DaprMQ gateway (bare host:port, no scheme).
*/}}
{{- define "daprmq-examples.gatewayGrpcAddress" -}}
{{ include "daprmq-examples.gatewayServiceName" . }}.{{ include "daprmq-examples.gatewayNamespace" . }}.svc.cluster.local:{{ .Values.daprmq.grpcPort }}
{{- end }}
