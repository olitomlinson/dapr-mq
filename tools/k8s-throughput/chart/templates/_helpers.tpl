{{- define "tp.queueId" -}}
{{- required "runId is required" .Values.runId | toString | printf "tp-%s" -}}
{{- end }}

{{- define "tp.consumerName" -}}
{{ .Release.Name }}-consumer
{{- end }}

{{- define "tp.labels" -}}
app.kubernetes.io/name: daprmq-throughput-test
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{- define "tp.image" -}}
{{ .Values.image.repository }}:{{ .Values.image.tag }}
{{- end }}

{{/* Environment shared by the consumers and the Job. */}}
{{- define "tp.env" -}}
- name: QUEUE_ID
  value: {{ include "tp.queueId" . | quote }}
- name: DAPRMQ_HTTP_BASE_URL
  value: "http://{{ .Values.daprmq.gatewayService }}.{{ .Values.daprmq.namespace }}.svc.cluster.local:{{ .Values.daprmq.httpPort }}"
- name: DAPRMQ_GRPC_ADDRESS
  value: "http://{{ .Values.daprmq.gatewayService }}.{{ .Values.daprmq.namespace }}.svc.cluster.local:{{ .Values.daprmq.grpcPort }}"
- name: SCALER_ADDRESS
  value: "http://{{ .Values.daprmq.operatorService }}.{{ .Values.daprmq.namespace }}.svc.cluster.local:{{ .Values.daprmq.operatorGrpcPort }}"
- name: SCALED_OBJECT_NAME
  value: {{ include "tp.consumerName" . | quote }}
- name: POD_NAMESPACE
  valueFrom:
    fieldRef:
      fieldPath: metadata.namespace
- name: WORKLOAD
  value: {{ .Values.workload | quote }}
- name: HANDLERS
  value: {{ .Values.consumer.handlers | quote }}
- name: WINDOW
  value: {{ .Values.consumer.window | quote }}
- name: ITEM_DELAY_MS
  value: {{ .Values.consumer.itemDelayMs | quote }}
- name: LOCK_TTL_SECONDS
  value: {{ .Values.consumer.lockTtlSeconds | quote }}
- name: SESSIONS
  value: {{ .Values.sessions.count | quote }}
- name: CONCURRENT_SESSIONS
  value: {{ .Values.sessions.concurrent | quote }}
- name: PREFETCH
  value: {{ .Values.sessions.prefetch | quote }}
- name: LEASE_SECONDS
  value: {{ .Values.sessions.leaseSeconds | quote }}
- name: SESSION_IDLE_TIMEOUT_SECONDS
  value: {{ .Values.sessions.idleTimeoutSeconds | quote }}
- name: REPLICAS
  value: {{ ternary .Values.scaling.replicas .Values.scaling.maxReplicas (eq .Values.scaling.mode "fixed") | quote }}
- name: SCALING_MODE
  value: {{ .Values.scaling.mode | quote }}
{{- end }}
